# EnglishApp

Gerçek hayat senaryolarıyla İngilizce öğrenme uygulaması. Detaylar: [docs/01_PROJE_DOKUMANI.md](docs/01_PROJE_DOKUMANI.md), [docs/02_DB_TASARIMI.md](docs/02_DB_TASARIMI.md).

## Projeler

| Proje | Ne yapar | Lokal adres |
|---|---|---|
| `src/EnglishApp.Api` | .NET 10 Web API: tüm iş mantığı, Dapper, JWT, FSRS, mail | http://localhost:5163 |
| `src/EnglishApp.Web` | .NET 10 MVC: arayüz + BFF proxy (`/bff/*` → API) | http://localhost:5076 |
| `src/EnglishApp.Shared` | Ortak DTO ve enum'lar | |
| `tests/EnglishApp.Tests` | Unit testler (FSRS, şifreleme, seri, kelime eşleme) | |

## Lokal kurulum

1. DB (tek seferlik): `database/` altındaki script'ler numara sırasıyla çalıştırılır (`000` → `011`). `003` ve sonrası tekrar çalıştırmaya güvenlidir. Dosyalar UTF-8 (BOM'lu); sqlcmd ile çalıştırırken `-f 65001` ver, yoksa Türkçe karakterler bozulur.
   ```bash
   sqlcmd -S .\SQL2025 -U sa -P *** -C -I -f 65001 -d master -i database/000_CreateDatabase.sql
   ```
2. Ayar dosyaları (gizli bilgiler repo'da yok): `src/EnglishApp.Api` ve `src/EnglishApp.Web` içindeki `appsettings.Development.example.json` dosyalarını `appsettings.Development.json` adıyla kopyalayıp değerleri doldur. `Bff:SharedKey` (API) ile `Api:BffKey` (Web) aynı olmalı.
3. Visual Studio'da Solution > **Configure Startup Projects** > **Multiple startup projects**: `EnglishApp.Api` ve `EnglishApp.Web` → Start.
4. API ilk açılışta:
   - hiç admin yoksa `appsettings.Development.json` > `Seed` bilgileriyle admin oluşturur (ilk girişte şifre değiştirmek zorunlu),
   - `Content/seed-content.json` içindeki senaryoları yükler (zaten varsa atlar).
5. Testler: `dotnet test`

## Sesler

| Ne | Nasıl | Nerede |
|---|---|---|
| Kelime, örnek cümle, senaryo diyalogları | **Kokoro** ile önceden üretilmiş MP3, Amerikan + İngiliz aksanı | `src/EnglishApp.Api/Content/audio/` (**repo'da yok**, ~170 MB; bu bilgisayardan publish edilirse yayına kopyalanır) |
| AI'ın konuşmadaki cevapları | **Gemini TTS** (anlık), sunucuda önbelleklenir | `App_Data/tts-cache/` |
| Yedek | Tarayıcının kendi sesi | MP3 yoksa veya Gemini kotası dolarsa |

Yeni içerik ekleyince (seed-content.json) sesleri üretmek için:
```bash
cd tools/audiogen
python -m venv .venv
.venv\Scripts\pip install -r requirements.txt
.venv\Scripts\python generate_audio.py
```
AI ile üretilen yeni senaryolar ve kelime havuzu için: **Admin > İçerik > Eksik sesler > Listeyi indir** ile `audio-manifest.json` dosyasını `tools/audiogen` klasörüne koy, sonra:
```bash
.venv\Scripts\python generate_audio.py --manifest-url audio-manifest.json
```
Üretilen MP3'leri `src/EnglishApp.Api/Content/audio/` klasörüne (canlıda sunucudaki aynı klasöre) kopyala.

> Ses dosyaları GitHub'a girmez (`.gitignore`). Yeni bir bilgisayarda klonlayınca `generate_audio.py` ile yeniden üretilir (`--manifest-url` ile tümü) ya da mevcut `Content/audio` klasörü kopyalanır. Canlıya alırken bu klasör sunucudaki `Content/audio` altına ayrıca kopyalanmalı.

Var olan dosyalar atlanır. İlk çalıştırmada Kokoro modeli (~340 MB) `tools/audiogen/models/` altına iner (repo'ya girmez).
Dosya adı kuralı `src/EnglishApp.Shared/Common/AudioNaming.cs` ile aynıdır, `AudioTests` bunu kontrol eder.

## Canlıya alma

.NET 10 ve SQL Server destekleyen herhangi bir sunucuya kurulabilir (Windows/IIS, Linux, Docker vb.). API ve Web ayrı sitelere publish edilir; veritabanı için `database/` altındaki script'ler sırayla çalıştırılır. Her biri için `appsettings.Production.json` oluştur (repo'ya girmez):

**API**
```json
{
  "ConnectionStrings": { "Default": "SQL Server bağlantı cümlesi" },
  "Jwt": { "Key": "en az 32 karakter, lokaldekinden FARKLI" },
  "Encryption": { "Key": "32 byte base64, lokaldekinden FARKLI" },
  "Bff": { "SharedKey": "Web ile aynı rastgele anahtar" },
  "Seed": { "AdminEmail": "...", "AdminPassword": "geçici şifre" }
}
```

**Web**
```json
{
  "Api": { "BaseUrl": "https://api-adresin", "BffKey": "API ile aynı anahtar" }
}
```

Sonra:
1. Admin > Ayarlar: SMTP bilgileri, `Jobs.SecretKey`, `General.AppUrl`. "Test maili gönder" ile SMTP'yi doğrula.
   Yapay zeka için **Gemini API anahtarı** (aistudio.google.com > Get API key, ücretsiz). İstersen yedek olarak OpenRouter (`:free` model) ve Groq anahtarları.
2. Bir zamanlayıcı servisinde (ör. cron-job.org) 15 dakikada bir: `POST https://api-adresin/api/v1/jobs/run`, header `X-Job-Key: {Jobs.SecretKey}`.

> Not: `Encryption:Key` değişirse DB'deki şifreli ayarlar (SMTP şifresi vb.) çözülemez, yeniden girilmesi gerekir.
