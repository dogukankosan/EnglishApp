"""
EnglishApp - Kokoro ile hazır ses (MP3) üretici.

Senaryo diyaloglarını, kelimeleri ve örnek cümleleri Kokoro (açık kaynak, ücretsiz) ile seslendirir,
src/EnglishApp.Api/Content/audio/ altına MP3 olarak yazar. API bu dosyaları otomatik bulur.

Dosya adı = sha256("ses|metin")[:20].mp3  ->  src/EnglishApp.Shared/Common/AudioNaming.cs ile AYNI kural.
Var olan dosyalar atlanır: yeni içerik ekleyince tekrar çalıştırmak yeterli.

Kullanım (tools/audiogen klasöründe):
    python -m venv .venv
    .venv\\Scripts\\pip install -r requirements.txt
    .venv\\Scripts\\python generate_audio.py
"""
import argparse
import hashlib
import json
import sys
import time
import urllib.request
from pathlib import Path

import lameenc
import numpy as np
from kokoro_onnx import Kokoro

# --- AudioNaming.cs ile aynı olmalı ---
ACCENTS = {
    "us": {"word": "af_heart", "dialogue": ["af_heart", "am_michael"], "lang": "en-us"},
    "uk": {"word": "bf_emma", "dialogue": ["bf_emma", "bm_george"], "lang": "en-gb"},
}

MODEL_FILES = {
    "kokoro-v1.0.onnx": "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/kokoro-v1.0.onnx",
    "voices-v1.0.bin": "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/voices-v1.0.bin",
}

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_SEED = ROOT / "src" / "EnglishApp.Api" / "Content" / "seed-content.json"
DEFAULT_OUT = ROOT / "src" / "EnglishApp.Api" / "Content" / "audio"
MODEL_DIR = Path(__file__).resolve().parent / "models"


def file_name(voice: str, text: str) -> str:
    return hashlib.sha256(f"{voice}|{text.strip()}".encode("utf-8")).hexdigest()[:20] + ".mp3"


def collect_items(seed_path: Path, accents):
    """(ses, metin, dil) üçlülerini toplar. Diyalogda konuşmacıya ilk görünme sırasına göre ses verilir."""
    data = json.loads(seed_path.read_text(encoding="utf-8"))
    items = {}
    for acc in accents:
        cfg = ACCENTS[acc]
        for sc in data["scenarios"]:
            for w in sc.get("words", []):
                for text in (w.get("lemma"), w.get("exampleEn")):
                    if text and text.strip():
                        items[(cfg["word"], text.strip())] = cfg["lang"]
            speakers = []
            for line in sc.get("dialogue", []):
                sp = line.get("speaker")
                if sp and sp not in speakers:
                    speakers.append(sp)
            for line in sc.get("dialogue", []):
                idx = speakers.index(line["speaker"]) if line.get("speaker") in speakers else 0
                voices = cfg["dialogue"]
                items[(voices[idx % len(voices)], line["en"].strip())] = cfg["lang"]
    return [(v, t, lang) for (v, t), lang in items.items()]


def fetch_manifest(url: str, token: str | None):
    """API'deki eksik ses listesini (voice, text, lang) çeker. Dosya adı kuralı aynı olduğu için doğrudan üretilebilir."""
    if not url.lower().startswith("http"):
        # Admin > İçerik ekranından indirilen JSON dosyası
        data = json.loads(Path(url).read_text(encoding="utf-8-sig"))
    else:
        req = urllib.request.Request(url, headers={"Authorization": f"Bearer {token}"} if token else {})
        with urllib.request.urlopen(req, timeout=60) as r:
            data = json.loads(r.read().decode("utf-8"))
    rows = data.get("data", data) if isinstance(data, dict) else data
    items = [(x["voice"], x["text"], x.get("lang", "en-us")) for x in rows]
    for v, t, _ in items:
        if file_name(v, t) != next(x["file"] for x in rows if x["voice"] == v and x["text"] == t):
            raise SystemExit("Dosya adı kuralı API ile uyuşmuyor (AudioNaming.cs).")
    return items


def ensure_models():
    MODEL_DIR.mkdir(parents=True, exist_ok=True)
    for name, url in MODEL_FILES.items():
        path = MODEL_DIR / name
        if path.exists() and path.stat().st_size > 1_000_000:
            continue
        print(f"Model indiriliyor: {name} (ilk seferde, ~300 MB) ...", flush=True)
        tmp = path.with_suffix(".part")
        urllib.request.urlretrieve(url, tmp)
        tmp.replace(path)


def to_mp3(samples: np.ndarray, sample_rate: int, bitrate: int) -> bytes:
    pcm = (np.clip(samples, -1.0, 1.0) * 32767).astype(np.int16).tobytes()
    enc = lameenc.Encoder()
    enc.set_bit_rate(bitrate)
    enc.set_in_sample_rate(sample_rate)
    enc.set_channels(1)
    enc.set_quality(2)  # 2 = yüksek kalite
    return enc.encode(pcm) + enc.flush()


def main():
    ap = argparse.ArgumentParser(description="Kokoro ile hazır MP3 sesleri üretir.")
    ap.add_argument("--seed", type=Path, default=DEFAULT_SEED)
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    ap.add_argument("--speed", type=float, default=0.95, help="Konuşma hızı (öğrenen için biraz yavaş: 0.95)")
    ap.add_argument("--bitrate", type=int, default=64, help="MP3 kbps (konuşma için 64 yeterli)")
    ap.add_argument("--force", action="store_true", help="Var olanları da yeniden üret")
    ap.add_argument("--accents", default="us,uk", help="Üretilecek aksanlar: us,uk")
    ap.add_argument("--manifest-url", help="Eksik ses listesi: Admin > İçerik ekranından indirilen JSON dosyası ya da API adresi (ör. http://localhost:5163/api/v1/admin/content/audio-manifest). AI ile üretilen yeni içerik için.")
    ap.add_argument("--token", help="--manifest-url için admin access token (Bearer)")
    args = ap.parse_args()

    if args.manifest_url:
        items = fetch_manifest(args.manifest_url, args.token)
    else:
        items = collect_items(args.seed, [a.strip() for a in args.accents.split(",") if a.strip()])
    args.out.mkdir(parents=True, exist_ok=True)
    todo = [(v, t, lang) for v, t, lang in items if args.force or not (args.out / file_name(v, t)).exists()]
    print(f"Toplam {len(items)} ses, üretilecek {len(todo)}.", flush=True)
    if not todo:
        return 0

    ensure_models()
    kokoro = Kokoro(str(MODEL_DIR / "kokoro-v1.0.onnx"), str(MODEL_DIR / "voices-v1.0.bin"))

    started = time.time()
    failed = 0
    for i, (voice, text, lang) in enumerate(todo, 1):
        try:
            samples, sr = kokoro.create(text, voice=voice, speed=args.speed, lang=lang)
            (args.out / file_name(voice, text)).write_bytes(to_mp3(samples, sr, args.bitrate))
            print(f"[{i}/{len(todo)}] {voice}: {text[:60]}", flush=True)
        except Exception as ex:  # tek bir cümle bozuksa diğerleri devam etsin
            failed += 1
            print(f"[{i}/{len(todo)}] HATA {voice}: {text[:60]} -> {ex}", file=sys.stderr, flush=True)

    print(f"Bitti: {len(todo) - failed} üretildi, {failed} hata, {time.time() - started:.0f} sn.", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
