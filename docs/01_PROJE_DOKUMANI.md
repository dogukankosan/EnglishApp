# EnglishApp — Proje Dokümanı

> Kişisel İngilizce öğrenme uygulaması. Tek kişi için başladı, çok kullanıcılı tasarlandı.
> Son güncelleme: 25.10.2026

---

## 1. Amaç

- **Hedef:** 1 yılda A2 → B1/B2. Yurt dışında anlaşabilmek: otel, havalimanı, restoran, eczane, alışveriş, genel sohbet.
- **Öncelik:** Önce **anlamak** (dinleme + kelime), sonra konuşmak.
- **Çalışma süresi:** Günde ~2 saat (akşam).
- **Yaklaşım:** Sınav hazırlığı değil, **gerçek hayat senaryoları**. Her şey bir senaryo etrafında döner, kelimeler cümle içinde ve sesli öğrenilir.

## 2. Öğrenme Metodu (bilimsel temel)

| İlke | Uygulamadaki karşılığı |
|---|---|
| Anlaşılır girdi (Krashen, i+1) | Senaryo diyalogları kullanıcının seviyesinin biraz üstünde üretilir |
| Aralıklı tekrar | **FSRS** algoritması. Her kart unutulmak üzereyken tekrar gelir |
| Hatırlamaya zorlama (retrieval) | Kartlarda önce cevap gizli, kullanıcı hatırlamaya çalışır, sonra puan verir |
| Sık kelime önce | **NGSL** sıklık listesi. Önce en sık kullanılan kelimeler |
| Kalıplar (chunks) | "check in", "how much is", "could I get" gibi kalıplar kelime gibi öğretilir |
| Dinleme | Her kelime ve cümlenin sesi var. Dinleyip anlama kartları |

**Kurallar:**
- Günlük yeni kelime limiti varsayılan **15** (kullanıcı ayarından değiştirilir). Tekrar yükü patlamasın.
- Başlangıçta **seviye testi** var. Bilinen kelimeler atlanır.
- Sürekli unutulan kelimeler **"zorlandıklarım"** listesine düşer, bunlar için farklı örnek cümle üretilir.

## 3. Modüller ve Fazlar

### Faz 1 — Temel
- Login, JWT, rol yönetimi (Admin / User)
- Admin: kullanıcı yönetimi, sistem ayarları, log görüntüleme
- Profil: ad, resim, şifre, bildirim tercihleri
- Senaryo bazlı **kelime çalışma** (FSRS)
- Senaryo bazlı **cümle çalışma** (dinleme-anlama, boşluk doldurma)
- **Durumum paneli:** tahmini seviye, öğrenilen / zorlanılan kelimeler, günlük-haftalık istatistik, seri, ısı haritası
- Seviye testi
- Mail: günlük hatırlatma, haftalık rapor (kullanıcı bazlı, opsiyonel)

### Faz 2 — AI
- **AI ile sesli rol yapma** (resepsiyonist, garson, eczacı...)
- **Yazma çalışması:** kullanıcı yazar, AI düzeltir ve Türkçe açıklar
- Admin: senaryo içeriğini Gemini ile üretme + onaylama
- Kullanıcı bazlı günlük token limiti

### Faz 3 — Genişleme
- Okuma metinleri (seviyeye ve bilinen kelimelere göre)
- Video / dizi / podcast önerileri
- Aylık dinleme-anlama testi

## 4. Mimari

```
EnglishApp.sln
├── src/
│   ├── EnglishApp.Api      (.NET 10 Web API)  → tüm iş mantığı, DB, LLM, mail, JWT
│   ├── EnglishApp.Web      (.NET 10 MVC)      → UI: Bootstrap 5, jQuery AJAX, SweetAlert2
│   └── EnglishApp.Shared   (class library)    → DTO'lar, enum'lar, ortak sabitler
├── database/                                    → SQL script'leri
├── tests/
│   └── EnglishApp.Tests                         → unit testler (FSRS, şifreleme, limitler)
└── docs/
```

**API içi katmanlar** (tek projede klasörlerle):
- `Endpoints/` veya `Controllers/` → HTTP katmanı, ince
- `Services/` → iş mantığı (tüm kurallar burada)
- `Repositories/` → Dapper ile veri erişimi
- `Infrastructure/` → Gemini/OpenRouter istemcileri, MailKit, şifreleme, saat (TrClock), loglama

### Temel kurallar
1. **Tüm iş mantığı ve doğrulama API'de.** UI'daki doğrulama sadece kullanıcı kolaylığı içindir. API, UI'a hiç güvenmez. İleride mobil uygulama aynı API'yi kullanacak.
2. **API sürümlü:** `/api/v1/...`
3. **Standart yanıt formatı:**
   ```json
   { "success": true, "data": { }, "error": null, "correlationId": "..." }
   ```
   Hata durumunda `error: { "code": "USER_NOT_FOUND", "message": "Kullanıcı bulunamadı", "details": [...] }`
4. **Girdi doğrulama:** FluentValidation (API tarafında).
5. **Rate limiting:** .NET'in yerleşik rate limiter'ı (login için sıkı, genel için gevşek).

## 5. Güvenlik

### Kimlik doğrulama akışı
1. Kullanıcı MVC'deki login sayfasından giriş yapar.
2. MVC sunucu tarafında `POST /api/v1/auth/login` çağırır → **access token (15 dk)** + **refresh token (7 gün)** alır.
3. MVC bunları **httpOnly + Secure + SameSite=Strict, şifreli cookie** içinde saklar. Tarayıcıdaki JS token'ı göremez.
4. Sayfadaki AJAX istekleri MVC'ye gelir (antiforgery token ile). MVC isteği `Authorization: Bearer <token>` ile API'ye iletir.
5. Access token süresi dolunca MVC refresh token ile otomatik yeniler.
6. Mobil uygulama ileride API'ye doğrudan Bearer ile bağlanır.

### Kurallar
- Şifreler `PasswordHasher` (PBKDF2, tuzlu) ile hash'lenir. Düz metin hiçbir yerde yok.
- Refresh token DB'de **SHA-256 hash'i** ile saklanır, her kullanımda yenilenir (rotation). Çalınmış token tekrar kullanılırsa kullanıcının tüm oturumları kapatılır.
- Şifre değişince `SecurityStamp` değişir → eski tüm token'lar geçersiz olur.
- 5 hatalı login → 15 dakika kilit.
- **Güvenlik doğrulaması (Cloudflare Turnstile, ücretsiz):** aynı IP'den veya aynı e-postaya 30 dakika içinde 3 hatalı denemeden sonra giriş ekranında "insan mısın" kutusu çıkar; token'ı API, Cloudflare'e sorarak doğrular (Cloudflare'e ulaşılamazsa geçmez). Sayaç bellekte, başarılı girişte sıfırlanır; kullanıcı var/yok ayrımı yapmaz. Anahtarlar Admin > Ayarlar > Güvenlik (gizli anahtar şifreli); boşsa doğrulama kapalı. `GET /api/v1/auth/login-challenge`, `database/010_Turnstile.sql`.
- **Kullanıcı ayrımı:** kişisel kayıtlar (kart, sohbet, yazı, video, okuma, seyahat kiti, yakalanan cümle, dikte) her sorguda token'daki kullanıcı numarasıyla filtrelenir; istemciden kullanıcı numarası alınmaz. Başkasının kaydı istenirse 404 döner. Başkasının kişisel kelime/cümlesine kart açılamaz (26.10.2026'da iki test kullanıcısıyla denendi ve kapatıldı). Admin uçları `Admin` rolü ister; imzası bozulan veya `alg=none` token reddedilir.
- **Bot tuzağı (honeypot):** giriş formunda ekran dışında, ekran okuyuculara kapalı `Website` alanı. Doluysa istek API'ye gitmeden reddedilir, genel bir hata gösterilir ve loglanır.
- **Arama motorları:** `robots.txt` (Disallow: /), tüm sayfalarda `<meta name="robots" content="noindex, nofollow">`, Web ve API yanıtlarında `X-Robots-Tag: noindex, nofollow` başlığı.
- **Cloudflare proxy (canlıya alırken):** DDoS koruması + Bot Fight Mode. Kendi alan adı gerekir. Açılınca gerçek kullanıcı IP'si `CF-Connecting-IP` başlığından okunmalı (rate limit ve loglar için).
- **Rate limit:** API: giriş/yenileme/şifre IP başına 10/dk, AI kullanıcı başına 20/dk, genel 300/dk. Web: giriş ve şifre değiştirme formu IP başına 10/dk, genel IP başına 600/dk (statik dosyalar hariç); aşılınca sayfada "Biraz yavaşla" (429) sayfası, AJAX'ta JSON hata.
- Pasif veya silinmiş kullanıcı giriş yapamaz, mevcut token'ı da reddedilir.
- **Şifre politikası:** en az 8 karakter, en az 1 harf ve 1 rakam.
- **İlk admin:** API ilk açılışta hiç admin yoksa `appsettings.json`'daki bilgilerle admin oluşturur, ilk girişte şifre değiştirmeyi zorunlu tutar.
- SQL bağlantısı: lokalde `sa`, canlıda hosting'in verdiği DB kullanıcısı. Bağlantı bilgisi `appsettings.json`'da.
- Tüm SQL sorguları **parametreli** (Dapper). String birleştirerek SQL yazılmaz.

### Yetkiler

| İşlem | Admin | User |
|---|---|---|
| Kullanıcı açma / düzenleme / pasife alma / silme | ✅ | ❌ |
| Kullanıcının rolünü ve token limitini ayarlama | ✅ | ❌ |
| Kendi adını, resmini, şifresini değiştirme | ✅ | ✅ |
| Kendi bildirim tercihleri | ✅ | ✅ |
| Kendi hesabını silme / pasife alma | ❌ | ❌ |
| Sistem ayarları (Gemini, SMTP), loglar | ✅ | ❌ |
| Senaryo üretme / onaylama | ✅ | ❌ |
| Çalışma, istatistik | ✅ | ✅ |

Admin kendi hesabını pasife alamaz ve silemez (sistemde admin kalmama riskine karşı).

### Şifreleme
- `IEncryptionService` → **AES-256-GCM**.
- Ana anahtar `appsettings.json`'da (`Encryption:Key`, base64). Kodda sabit değer yok.
- DB'deki `app.Settings` tablosunda hassas değerler (`IsEncrypted = 1`) şifreli tutulur: Gemini anahtarı, SMTP şifresi, cron gizli anahtarı.
- GitHub'a atarken `appsettings.*.json` içindeki gizli bilgiler repo dışında tutulur.

## 6. Veritabanı

- **SQL Server 2025** (lokal: `.\SQL2025`)
- **Dapper** ile erişim. EF Core yok.
- Tüm sorgular async, kısa ömürlü bağlantı (connection pool).
- Detaylar: `docs/02_DB_TASARIMI.md`

## 7. Saat

- **Her yerde Türkiye saati.** Sunucu yabancı olduğu için kodda `DateTime.Now` / `DateTime.UtcNow` doğrudan kullanılmaz.
- Merkezi `ITrClock` servisi → `TimeZoneInfo` ile `Turkey Standard Time`.
- DB'deki varsayılan değerler de Türkiye saatine çevrilir (`SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time'`).

## 7.1 Türkçe / Kültür Kuralları (sunucu İngilizce)

Sunucunun dili, saati ve collation'ı Türkiye'den farklı. Bu yüzden:

- **DB collation:** `Latin1_General_100_CI_AS`. Türkçe metinler `nvarchar`'da sorunsuz saklanır.
- **Büyük/küçük harf:** `ToLower()` / `ToUpper()` yerine `ToLowerInvariant()` / `ToUpperInvariant()`. (Türkçe kültürde `"I".ToLower()` = `"ı"` olur, e-posta ve İngilizce kelime karşılaştırmalarını bozar.)
- **String karşılaştırma:** `StringComparison.OrdinalIgnoreCase`. Kültüre bağlı karşılaştırma yok.
- **API'de sayı ve tarih:** her zaman `InvariantCulture` (`0.90`, ISO tarih `2026-10-25T20:00:00`). Virgül/nokta karışmaz.
- **UI'da gösterim:** `tr-TR` (MVC'de RequestLocalization ile sabit). Tarih `25.10.2026 20:00`, sayı `1.250`.
- **Sunucunun varsayılan kültürüne asla güvenilmez.** Uygulama açılışında kültür açıkça ayarlanır.

## 8. LLM (Yapay Zeka)

| Rol | Sağlayıcı | Not |
|---|---|---|
| Ana | **Gemini Flash** (ücretsiz katman) | İçerik üretimi, rol yapma, yazma düzeltme |
| Hafif işler | **Gemini Flash-Lite** | Kelime açıklama gibi küçük istekler |
| Yedek | **OpenRouter `:free`** / **Groq** ücretsiz | Gemini kotası dolarsa otomatik geçiş |

- **Tamamen ücretsiz.** Ücretli API kullanılmaz.
- Tüm sağlayıcılar tek bir `ILlmClient` arayüzü arkasında (OpenAI uyumlu uç noktalar).
- Tek ortak Gemini anahtarı, tüm kullanıcılar kullanır.
- **Kullanıcı bazlı günlük token limiti** (admin ayarlar, boşsa sınırsız). Limit dolarsa AI özellikleri o gün kapanır, kelime/cümle çalışma devam eder.
- Üretilen ortak içerik (senaryo, kelime açıklaması) DB'de saklanır, tekrar üretilmez (`ai.ResponseCache`).
- İçerik üretiminde Gemini'den **JSON şemasına uygun** yanıt istenir, doğrulanıp kaydedilir.
- AI ile üretilen senaryolar **admin onayından** sonra yayına girer.
- Not: Gemini ücretsiz katmanında gönderilen veriler Google tarafından model eğitiminde kullanılabilir. Kullanıcılara bilgi verilir.

## 9. Ses

- **Konuşma → yazı:** tarayıcının Web Speech API'si (Chrome / Edge).
- **Yazı → ses:** tarayıcının konuşma sentezi (Edge'deki doğal Microsoft sesleri önerilir). Gerekirse ileride sunucuda üretilip dosya olarak saklanır.
- Önerilen tarayıcı: **Edge** veya **Chrome**.

## 10. Mail

- **MailKit** ile, SMTP ayarları admin panelinden (DB'de şifreli).
- Kullanıcı bazlı, kullanıcı profilinden açılıp kapatılır:
  - **Günlük hatırlatma ve özet** (saati kullanıcı seçer): çalışmadıysa / bekleyen tekrarı varsa hatırlatma; o günün işini bitirdiyse günün özeti (kart, yeni kelime, süre, doğruluk, seri)
  - **Haftalık rapor** (günü kullanıcı seçer): bu hafta öğrenilenler, tekrar edilmesi gerekenler, zorlandıkların, performans + AI koç yorumu
  - **Aylık rapor** (açılıp kapatılır, ayın ilk 3 günü, geçen ay için): çalışılan gün/süre/kart/yeni kelime/doğruluk, en uzun seri, kelime dağarcığı ve tahmini seviye, AI pratik özeti, bu ay öğrenilen kelimelerden örnekler, zorlanılan kelimeler, aylık sıralama ve kısa değerlendirme yorumu. `database/011_Monthly_Report.sql`
- Admin > Ayarlar'dan günlük / haftalık / aylık mailin örneği kendi verisiyle admin'in kendi adresine gönderilebilir (`POST /api/v1/admin/settings/sample-report/{daily|weekly|monthly}`).
- Tetikleme: dış bir cron servisi (ör. cron-job.org) API'deki korumalı `/api/v1/jobs/run` adresini 15 dakikada bir çağırır (gizli anahtar ile). IIS uyku moduna geçse de çalışır.
- Aynı mail aynı dönemde iki kez gitmez (`LastReminderSentDate` / `LastWeeklyReportSentDate` / `LastMonthlyReportSentDate`).
- Tüm gönderimler `app.EmailLogs` tablosuna yazılır.
- Sunucuda dış SMTP portunun açık olduğu kurulumda test maili ile doğrulanır.

## 11. Loglama

- **Serilog**
  - Dosya: günlük dönen dosyalar, tüm seviyeler
  - SQL: `app.Logs` tablosu, **Warning ve üstü**
- **Global hata yakalayıcı** middleware (API ve MVC'de): yakalanmamış her hata detaylı loglanır (kullanıcı, adres, parametreler, stack trace). Kullanıcıya sadece "Bir hata oluştu. Kod: XXXX" gösterilir, iç detay sızmaz.
- **Kritik noktalarda try-catch:** LLM çağrıları (yedeğe geçiş), mail gönderimi, login/token, dosya yükleme, DB yazma işlemleri, cron işleri. Yakalanan hata **mutlaka loglanır**, sessizce yutulmaz.
- **Correlation ID:** MVC'den API'ye her istek `X-Correlation-Id` başlığını taşır. Bir hatanın iki projedeki izi tek numarayla bulunur.
- **Hassas veri loglanmaz:** şifre, token, API anahtarı maskelenir.
- Her API isteğinin süresi loglanır, yavaş istekler (>1 sn) Warning olur.
- **Admin işlemleri** ayrıca `app.AuditLogs` tablosuna yazılır (kim, neyi, eski değer, yeni değer).
- Admin panelinde log görüntüleme sayfası (filtre: seviye, tarih, kullanıcı, correlation ID).
- Eski log temizliği: `app.Logs` 90 gün, `ai.Usage` 180 gün.

## 12. UI

- **Bootstrap 5**, mobil öncelikli, tamamen **responsive**
- **jQuery AJAX**: sayfa yenilenmeden çalışma
- **SweetAlert2**: bildirim, onay, hata mesajları
- **DataTables**: admin listeleri (arama, sayfalama)
- **Chart.js**: istatistik grafikleri
- Arayüz dili **Türkçe**. İçerik İngilizce, açıklamalar Türkçe.
- Sade ve kullanımı kolay. Çalışma ekranları telefonda tek elle kullanılabilir olmalı.

### "Durumum" paneli
- Tahmini seviye (sıklık bantlarındaki bilinen kelimelere göre)
- Öğrenilen kelimeler / zorlanılan kelimeler
- Bugün ve bu hafta: çalışılan kart, doğruluk oranı, süre
- Seri (streak) ve takvim ısı haritası
- Senaryo bazlı ilerleme (Otel %80, Havalimanı %40...)

## 13. Hosting

- .NET 10 ve SQL Server destekleyen herhangi bir hosting (Windows/IIS, Linux, Docker vb.). API ve Web ayrı siteler.
- Kaynak kod: GitHub (private).

## 14. İçerik Kaynakları

- **Kelime listesi:** NGSL (New General Service List), açık lisanslı sıklık listesi
- **Senaryolar:** Gemini ile üretilir, admin onaylar
- İlk senaryo seti (öneri): Havalimanı, Otel, Restoran/Kafe, Taksi/Ulaşım, Alışveriş, Eczane/Hastane, Yol sorma, Tanışma/Sohbet, Acil durum, Telefonda konuşma

## 15. Faz 1 Durumu (25.10.2026)

Tamamlandı: login/JWT/refresh token, zorunlu şifre değişimi, admin (kullanıcı, ayarlar, loglar), profil (resim, ayarlar, bildirimler), senaryo listesi ve diyalog dinleme, FSRS kart çalışması, seviye testi, Durumum paneli, Kelimelerim, günlük hatırlatma ve haftalık rapor mailleri (cron ile), loglama, 38 unit test.

Uygulamada netleşen kararlar:
- **BFF anahtarı:** İstekler MVC üzerinden gittiği için API, gerçek kullanıcı IP'sini MVC'nin gönderdiği `X-Client-Ip` başlığından alır; sadece ortak gizli anahtar (`Bff:SharedKey` = `Api:BffKey`) doğruysa güvenir.
- **Şema değişiklikleri:** DbUp yerine `database/` altında numaralı SQL script'leri elle çalıştırılır.
- **Profil resmi:** Boyut küçültme yok (ek kütüphane gerekmesin diye). Tip içerikten kontrol edilir, en fazla 2 MB.
- **Başlangıç içeriği:** 6 senaryo, 73 kelime/kalıp, 84 diyalog satırı (`src/EnglishApp.Api/Content/seed-content.json`).

## 15.1 Faz 2 Durumu (25.10.2026)

Tamamlandı:
- **AI rol yapma** (`/Practice/Talk`): senaryo seçilir, AI hizmet veren rolü (resepsiyonist, garson, taksici…) oynar, kullanıcı müşteri olur. Serbest sohbet modu da var.
  - Her kullanıcı mesajına anında düzeltme + kısa Türkçe açıklama, AI cevabının Türkçe çevirisi (varsayılan gizli: önce anlamaya çalış), 2 örnek cevap önerisi.
  - Ses: tarayıcının konuşma tanıması (mikrofon) + seslendirme. "Eller serbest" modunda AI konuşunca mikrofon otomatik açılır, konuşunca mesaj otomatik gider.
  - "Bitir" ile değerlendirme: puan, güçlü yanlar, üzerinde çalışılacak hatalar, işe yarayacak kalıplar.
- **Yazma çalışması** (`/Practice/Write`): 10 hazır gerçek hayat görevi + serbest yazı. Puan, tahmini seviye, düzeltilmiş hal, Türkçe açıklamalı hatalar, doğal (native) hali, öğrenilecek kalıplar. Taslak tarayıcıda saklanır.
- **LLM katmanı:** tek OpenAI uyumlu istemci; sıra Gemini ana model → Gemini Lite → Groq → OpenRouter (kota/hata olunca sıradakine geçer). Gemini'de JSON şeması zorunlu tutulur, diğerlerinde JSON modu + doğrulama.
- **Token limiti:** her çağrıdan önce kullanıcının günlük limiti kontrol edilir; kullanım `ai.Usage` ve `study.DailyStats.AiTokens`'a yazılır. AI uçlarında ayrıca kullanıcı başına dakikada 20 istek sınırı var.
- DB değişikliği: `database/003_Faz2_AI.sql`.

Notlar:
- Gerçek Gemini anahtarıyla henüz denenmedi; akış yerel sahte bir sunucuyla uçtan uca test edildi. Geliştirmede `Llm:BaseUrls:Gemini` ayarıyla adres değiştirilebilir (canlıda tanımlanmaz).
- Varsayılan modeller: `gemini-3.6-flash` (ana) ve `gemini-3.5-flash-lite`. `gemini-flash-latest` en yeni sürümü gösterdiği için sık sık yoğun (503) olduğundan bırakıldı. Yedekler: Groq `openai/gpt-oss-120b`, OpenRouter `nvidia/nemotron-3.5-lightning:free`. Admin > Ayarlar > "Yapay zekayı test et" ile kontrol edilebilir.

## 15.2 Gerçekçi Ses ve Hız (26.10.2026)

- **Hazır sesler (Kokoro):** 229 MP3 (73 kelime/kalıp, 73 örnek cümle, 83 diyalog satırı), toplam 3,4 MB. Diyalogda 1. konuşmacı kadın (`af_heart`), 2. erkek (`am_michael`). Üretici: `tools/audiogen/generate_audio.py`.
- **AI cevapları (Gemini TTS):** `POST /api/v1/ai/tts`. Sıra: `gemini-3.8-flash-lite-tts` → `gemini-3.1-flash-tts-preview`. Aynı rol hep aynı sesle konuşur. Üretilen ses önbelleğe alınır (tekrar dinleme kota harcamaz). Kotası dolan model 10 dk dinlendirilir. Hepsi başarısızsa tarayıcı sesine geçilir. TTS kullanımı da günlük token limitine sayılır.
- **Hız:** Sohbette önce Gemini Lite, düşünme "minimal". LLM zaman aşımı 45 → 25 sn (takılan model uzun süre bekletmez).
- DB: `database/005_Audio_Speed.sql`.

## 15.3 Ses Tanıma (26.10.2026)

- Tarayıcı ses tanıması (Web Speech API) sesi Google'a gönderir; **Brave ve Firefox'ta çalışmaz** ("network" hatası).
- Çözüm: **Groq Whisper** (`whisper-large-v3-turbo`, ücretsiz) ile sunucu tarafı tanıma: `POST /api/v1/ai/stt`. Tarayıcı mikrofonu kaydeder, konuşma bitince (~1,3 sn sessizlik) kendiliğinden durur, ses API'ye gönderilip yazıya çevrilir (~0,5 sn).
- Konuşma ekranında seçim: **Otomatik** (tarayıcı denenir, hata verirse bu tarayıcı için kalıcı olarak Whisper'a geçilir) / Tarayıcı / Sunucu (Whisper).
- Whisper kullanımı da günlük token sayacına yazılır. Admin "Yapay zekayı test et" butonu ses tanımayı da hazır bir MP3 ile dener.
- DB: `database/006_Stt.sql`.

## 15.4 Pratik Özellikleri (26.10.2026)

- **Dinleme testi** (`/Practice/Listen`): senaryo diyaloğu metinsiz dinlenir (en fazla 3 kez), 5 Türkçe çoktan seçmeli soru. Sorular senaryo başına bir kez AI ile üretilip `content.ListeningQuizzes`'e kaydedilir; doğru cevaplar istemciye gönderilmez. Sonuçlar `study.QuizAttempts`. Son 30 günde test yoksa Durumum panelinde "aylık dinleme ölçümü" uyarısı çıkar.
- **Hız ve aksan:** her dinleme ekranında 0,75x / 1x / 1,25x ve Amerikan / İngiliz seçimi (tarayıcıda saklanır). İngiliz sesleri Kokoro `bf_emma` / `bm_george` ile üretildi (+229 MP3). DTO'larda `AudioUrlUk`.
- **Cümle yakalama** (`/Practice/Capture`): yapıştırılan cümleyi AI açıklar (çeviri, argo/kısaltma, öğrenilecek kalıplar); seçilenler kişisel kelime + cümle kartı olur.
- **Telaffuz (shadowing):** kartlarda, senaryo satırlarında, seyahat kiti ve yakalanan cümlelerde "Tekrar et": ses tanıma ile söylenen cümle hedefle kelime kelime (LCS) karşılaştırılır, doğru/yanlış kelimeler ve yüzde gösterilir. Sunucuya ek yük yok.
- **Seyahat kiti** (`/Practice/Travel`): ülke/şehir/amaç girilir, AI yerel ipuçları + 6-7 bölümde ~30 kalıp üretir (yerlilerin söyleyeceği ifadeler dahil); tek tıkla kişisel cümle kartlarına eklenir.
- **Kişisel içerik:** `content.Words/Sentences.OwnerUserId`. Başka kullanıcıya örnek cümle veya seviye testi kelimesi olarak görünmez. Kişisel kartlar günlük yeni kelime limitine takılmaz (kullanıcı kendisi eklediği için).
- DB: `database/007_Practice_Features.sql`.

## 15.5 Öğrenme Ekstraları (26.10.2026)

- **AI senaryo üretici** (Admin > İçerik): kategori + seviye + konu girilir, AI diyalog, kelimeler ve örnek cümlelerle **taslak** senaryo üretir. Admin kontrol edip yayınlar (senaryonun kelime ve cümleleri de yayına alınır), arşivler ya da siler. Çalışılmış senaryo silinemez. Taslak silinince ona özel açılan kelime ve örnek cümleler de silinir.
- **Genel kelime havuzu:** sıklık listesi (ör. NGSL ilk 3000) satır satır içeri alınır (`FrequencyRank`), "Anlamları AI ile doldur" 15'lik partilerle anlam, IPA ve örnek cümle üretir. Senaryo seçmeden çalışırken yeni kelime bütçesi kalırsa havuzdan sıklık sırasıyla kelime gelir.
- **Eksik sesler:** Admin > İçerik > "Listeyi indir" → `audio-manifest.json` → `generate_audio.py --manifest-url audio-manifest.json`. Hazır MP3 yoksa uygulama önce Gemini doğal sesini, o da yoksa tarayıcı sesini kullanır. Genel havuzun tüm sesleri (2860 kelime + örnek cümle, 2 aksan, ~11.000 dosya, ~170 MB) üretildi; ses dosyaları repo'ya girmez, canlıya ayrıca kopyalanır.
- **Dikte** (`/Practice/Dictation`): cümle dinlenir (hız/aksan seçilebilir), yazılır; LCS hizalaması ile kelime kelime doğru / küçük hata (Levenshtein ≤ 1, yarım puan) / kaçırıldı gösterilir. Bağlı konuşma ipuçları (gonna, wanna, d'ya, flap t...). Puanı 80'in altındaki cümleler sonraki setlerde önce gelir. `study.DictationAttempts`.
- **Okuma** (`/Practice/Reading`): kullanıcının bildiği kelimelerle (+ en fazla 8 yeni) AI kısa hikâye yazar; paragraf çevirisi, yeni kelime listesi, 4 anlama sorusu. Kelimeye dokununca önce sözlük, yoksa hızlı AI modeli anlamı verir; tek tıkla kelime + cümle kartı. `content.ReadingTexts`.
- **Ne izlesem?** (`/Practice/Media`): seviyeye göre dizi / film / YouTube / podcast önerileri, "Listemde / İzliyorum / Bitirdim". `content.MediaRecommendations`, `study.UserMedia` (`009_Media_Seed.sql`).
- **YouTube sahneleri** (`/Practice/Video`): YouTube linki + kullanıcının yapıştırdığı zaman damgalı transkript (YouTube panel metni, SRT veya VTT). Video cümle cümle oynatılır, sahne döngüsü, metni gizleme (dinleyerek anlama), "Yakala" ile cümle yakalamaya aktarma. YouTube altyazıların sunucudan otomatik çekilmesine izin vermediği için transkript elle yapıştırılır. `study.Videos`.
- **Haftalık AI koç:** son 7 günün verisi (kart istatistikleri, unutulan kelimeler, sohbet ve yazı hataları, zayıf dikteler, dinleme testleri) özetlenip modele gider; Türkçe özet, 3 odak noktası, kalıplar ve günlük plan döner. Hafta (Pazartesi) başına bir kayıt (`ai.CoachReports`). Durumum panelinde kart; haftalık rapor mailine de eklenir (üretilemezse mail koçsuz gider).
- **Hedef + dinlenme günü:** Profil > Hedefim: hedef seviye (kelime eşiği A2 500, B1 1000, B2 2500, C1 4000) ve tarih. Durumum panelinde ilerleme, gereken günlük kelime, son 14 günün temposuna göre "x gün öndesin / gerisindesin". Dinlenme günü açıksa her hafta (Pzt-Paz) 1 kaçırılan gün seriyi bozmaz.
- DB: `database/008_Learning_Extras.sql`, `database/009_Media_Seed.sql`.
- **Sıralama** (`/Leaderboard`, `GET /api/v1/leaderboard?period=today|week|month`): tüm aktif kullanıcılar (admin dahil) birbirinin özet durumunu görür: tahmini seviye, hedef seviye, seri, bugün çalıştı mı, dönemdeki kart / yeni kelime / süre / doğruluk, kelime dağarcığı. Puan = çalışılan kart + 2 × yeni kelime; eşit puanlılar aynı sırayı paylaşır. Hafta Pazartesi, ay ayın 1'inden başlar. E-posta, kullanıcı numarası ve içerik (kelimeler, sohbet, yazı) paylaşılmaz.

## 16. Açık Konular

- [ ] Canlı sunucuda dış SMTP testi
- [ ] Canlı veritabanında `ALTER DATABASE` izinleri
- [ ] NGSL listesinin içe aktarılması ve Türkçe anlamların üretilmesi (araç hazır: Admin > İçerik)
- [x] GitHub private repo
