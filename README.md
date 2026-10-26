# EnglishApp

<img width="1228" height="926" alt="web" src="https://github.com/user-attachments/assets/cce1245e-7cda-4d48-8448-dba32a9978fb" />


Gerçek hayat senaryolarıyla İngilizce öğrenme uygulaması. Hedef: yurt dışında insanları anlayabilmek (önce dinleme ve anlama, sonra konuşma). Tamamen ücretsiz servislerle çalışır.

Detaylar: [docs/01_PROJE_DOKUMANI.md](docs/01_PROJE_DOKUMANI.md), [docs/02_DB_TASARIMI.md](docs/02_DB_TASARIMI.md).

## Özellikler

- **Kart çalışma (FSRS):** senaryo kelimeleri + en sık kullanılan 2800+ kelime (NGSL), Türkçe anlam, telaffuz, örnek cümle, Amerikan/İngiliz sesi
- **Senaryolar:** havalimanı, otel, restoran, taksi, eczane, tanışma... diyalog dinleme ve okuma
- **AI pratik (Gemini, yedek: Groq/OpenRouter):** sesli rol yapma, serbest sohbet, yazma düzeltme, dinleme testi, cümle yakalama, seyahat kiti, dikte, kişiye özel okuma metni, YouTube sahneleri
- **Takip:** Durumum paneli, tahmini seviye, hedef ve tempo, seri (haftada 1 dinlenme günü), sıralama tablosu, haftalık AI koç
- **Mailler:** günlük hatırlatma/özet, haftalık ve aylık rapor (kullanıcı açıp kapatır)
- **Admin:** kullanıcı yönetimi, sistem ayarları (şifreli), loglar, AI ile senaryo üretme, kelime havuzu yükleme
- **Güvenlik:** JWT + refresh token rotasyonu, rol yetkisi, kullanıcı verisi ayrımı, rate limit (API + Web), Cloudflare Turnstile, bot tuzağı, CSRF, şifreli ayarlar, detaylı loglama

## Projeler

| Proje | Ne yapar | Lokal adres |
|---|---|---|
| `src/EnglishApp.Api` | .NET 10 Web API: tüm iş mantığı, Dapper, JWT, FSRS, mail, AI | http://localhost:5163 |
| `src/EnglishApp.Web` | .NET 10 MVC: arayüz + BFF proxy (`/bff/*` → API). Veritabanına bağlanmaz | http://localhost:5076 |
| `src/EnglishApp.Shared` | Ortak DTO ve enum'lar | |
| `tests/EnglishApp.Tests` | Birim testleri (FSRS, güvenlik, seri/hedef, dikte, sıralama, raporlar, log kuralları...) | |
| `tools/audiogen` | Kokoro ile hazır MP3 üretici (Python) | |

İş mantığının tamamı API'dedir; ileride mobil uygulama aynı API'yi kullanacak.

## Lokal kurulum

1. **Veritabanı (tek seferlik):** `database/` altındaki script'ler numara sırasıyla çalıştırılır (`000` → `011`). `003` ve sonrası tekrar çalıştırmaya güvenlidir. Dosyalar UTF-8 (BOM'lu); sqlcmd ile çalıştırırken `-f 65001` ver, yoksa Türkçe karakterler bozulur.
   ```bash
   sqlcmd -S .\SQL2025 -U sa -P *** -C -I -f 65001 -d master -i database/000_CreateDatabase.sql
   ```
2. **Ayar dosyaları** (gizli bilgiler repo'da yok): `src/EnglishApp.Api` ve `src/EnglishApp.Web` içindeki `appsettings.Development.example.json` dosyalarını `appsettings.Development.json` adıyla kopyalayıp doldur. API'deki `Bff:SharedKey` ile Web'deki `Api:BffKey` aynı olmalı.
3. Visual Studio'da Solution > **Configure Startup Projects** > **Multiple startup projects**: `EnglishApp.Api` ve `EnglishApp.Web` → Start.
4. API ilk açılışta:
   - hiç admin yoksa `Seed` bilgileriyle admin oluşturur (ilk girişte şifre değiştirmek zorunlu),
   - `Content/seed-content.json` içindeki senaryoları yükler (zaten varsa atlar).
5. **Kelime havuzu:** Admin > İçerik > Kelime havuzu → sıklık listesini (ör. [NGSL](https://www.newgeneralservicelist.com/new-general-service-list)) yapıştır, **İçeri al**, sonra **Anlamları AI ile doldur** (ücretsiz kotayla birkaç saat sürebilir).
6. Testler: `dotnet test`

## Sesler

| Ne | Nasıl | Nerede |
|---|---|---|
| Kelime, örnek cümle, senaryo diyalogları | **Kokoro** ile önceden üretilmiş MP3, Amerikan + İngiliz aksanı | `src/EnglishApp.Api/Content/audio/` (**repo'da yok**, ~135 MB) |
| AI cevapları, okuma metinleri | **Gemini TTS** (anlık), sunucuda önbelleklenir | `App_Data/tts-cache/` |
| Yedek | Tarayıcının kendi sesi | MP3 yoksa veya Gemini kotası dolarsa |

Sesleri üretmek için (ilk seferde Kokoro modeli ~340 MB `tools/audiogen/models/` altına iner, repo'ya girmez):
```bash
cd tools/audiogen
python -m venv .venv
.venv\Scripts\pip install -r requirements.txt
.venv\Scripts\python generate_audio.py
```
AI ile üretilen senaryolar ve kelime havuzu için: **Admin > İçerik > Eksik sesler > Listeyi indir** ile `audio-manifest.json` dosyasını `tools/audiogen` klasörüne koy, sonra:
```bash
.venv\Scripts\python generate_audio.py --manifest-url audio-manifest.json
```
Var olan dosyalar atlanır. Üretilen MP3'leri `src/EnglishApp.Api/Content/audio/` klasörüne (canlıda sunucudaki aynı klasöre) kopyala.

> Ses dosyaları GitHub'a girmez (`.gitignore`). Yeni bir bilgisayarda klonlayınca yeniden üretilir ya da mevcut `Content/audio` klasörü kopyalanır.
> Dosya adı kuralı `src/EnglishApp.Shared/Common/AudioNaming.cs` ile aynıdır, `AudioTests` bunu kontrol eder.

## Canlıya alma

.NET 10 ve SQL Server destekleyen herhangi bir sunucuya kurulabilir (Windows/IIS, Linux, Docker vb.). API ve Web ayrı sitelere publish edilir (`dotnet publish -c Release`). Yayına yerel gizli ayarlar (`appsettings.Development*.json`) ve gereksiz dil klasörleri girmez.

### 1. Veritabanı
- **Yerel veritabanının yedeğini geri yükleyeceksen** (`.bak`): kelimeler, senaryolar ve ayarlar hazır gelir. Bu durumda API'nin `Encryption:Key` değeri **yereldekiyle aynı** olmalı, yoksa şifreli ayarlar (AI anahtarları, SMTP şifresi) açılmaz.
- **Sıfırdan kuracaksan:** script'leri `000` → `011` sırayla çalıştır, sonra kelime havuzunu Admin > İçerik'ten doldur. Bu durumda `Encryption:Key` yeni ve farklı olmalı.

### 2. Ayar dosyaları (`appsettings.Production.json`, repo'ya girmez)

**API**
```json
{
  "ConnectionStrings": {
    "Default": "Server=...;Database=...;User Id=...;Password=...;Encrypt=False;MultipleActiveResultSets=False;Pooling=True;Max Pool Size=100;Connect Timeout=30;ConnectRetryCount=3;ConnectRetryInterval=5;Application Name=EnglishApp.Api"
  },
  "Jwt": { "Key": "en az 32 karakter, lokaldekinden FARKLI" },
  "Encryption": { "Key": "32 byte base64 (yukarıdaki nota bak)" },
  "Bff": { "SharedKey": "Web ile aynı rastgele anahtar" },
  "Seed": { "AdminEmail": "...", "AdminPassword": "geçici şifre (sadece sıfırdan kurulumda)" },
  "Hosting": { "HttpsRedirection": false, "ForwardedHeaders": false, "ForwardedForHeader": "X-Forwarded-For" }
}
```

**Web** (veritabanı bilgisi yok; loglarını API üzerinden yazar)
```json
{
  "Api": { "BaseUrl": "https://api-adresin", "BffKey": "API ile aynı anahtar" },
  "Hosting": { "HttpsRedirection": false, "ForwardedHeaders": false, "ForwardedForHeader": "X-Forwarded-For" }
}
```

**Hosting ayarları:**
- `HttpsRedirection`: HTTP→HTTPS yönlendirmesini hosting yapıyorsa `false` (çift yönlendirme / döngü olmasın). HSTS her durumda açık.
- `ForwardedHeaders`: hosting HTTPS'i bir proxy'de çözüp uygulamaya HTTP iletiyorsa `true` (gerçek protokol ve kullanıcı IP'si başlıklardan okunur). Cloudflare proxy için ayrıca `ForwardedForHeader: "CF-Connecting-IP"`.
- Her iki site HTTPS ile açılmalı (giriş çerezi sadece HTTPS'te çalışır) ve `App_Data` klasörüne yazabilmeli.

### 3. Admin > Ayarlar
- **Uygulamanın web adresi:** Web sitesinin adresi (maillerdeki linkler için)
- **Cron tetikleme gizli anahtarı:** uzun rastgele bir değer
- **Yapay zeka:** Gemini API anahtarı (aistudio.google.com > Get API key, ücretsiz); yedek olarak Groq ve OpenRouter (`:free` model)
- **E-posta (SMTP):** kaydetmeden önce test maili gönderilir; günlük/haftalık/aylık örnek mail butonlarıyla mailleri gör
- **Güvenlik:** Cloudflare Turnstile site ve gizli anahtarı; Cloudflare panelinde widget'ın Hostname listesine Web sitesinin alan adını ekle

### 4. Zamanlanmış işler
Bir zamanlayıcı servisinde (ör. cron-job.org) 15 dakikada bir:
`POST https://api-adresin/api/v1/jobs/run`, header `X-Job-Key: {cron tetikleme gizli anahtarı}`. Test çağrısı `200` ve `"success":true` dönmeli.
Hatırlatma ve raporlar bu çağrıyla gider; gece eski loglar temizlenir. Çağrı zaman aşımına uğrasa da iş yarıda kalmaz.

### 5. Güncelleme yaparken
- Publish yeni dosyaları yazar ama sunucudaki eskileri **silmez**; kaldırılan kütüphanelerin dosyaları sunucuda elle silinir.
- Sunucudaki `appsettings.Production.json`, `App_Data` ve `.well-known` (hosting'in sertifika klasörü) dosyalarının **üzerine yazma**.
- `Content/audio` sadece yeni ses üretildiyse kopyalanır.

---
Geliştirici: [Doğukan Koşan](https://github.com/dogukankosan)
