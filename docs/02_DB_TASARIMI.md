# EnglishApp — Veritabanı Tasarımı

> SQL Server 2025 · Dapper · Collation: `Latin1_General_100_CI_AS`
> Script'ler: `database/000_CreateDatabase.sql`, `001_Schema.sql`, `002_SeedData.sql`

## 1. Genel Kurallar

| Kural | Neden |
|---|---|
| Tarih kolonları `datetime2(0)`, **Türkiye saati** | Sunucu yabancı. Varsayılan değerler `AT TIME ZONE 'Turkey Standard Time'` ile çevrilir |
| PK'lar `int`/`bigint IDENTITY` | Sıralı ekleme, küçük index. GUID clustered key yok |
| Enum'lar `tinyint` + `CHECK` | 1 byte, C# enum ile eşleşir |
| Metinler boyutlu `nvarchar(n)` | `max` sadece AI mesajları ve loglar gibi gerçekten uzun alanlarda |
| Soft delete sadece `auth.Users`'ta | Diğer tablolarda gerek yok |
| Trigger, cursor, UDF yok | Performans |
| Collation `Latin1_General_100_CI_AS` | Sunucu `Turkish_CI_AS`. Türkçe collation'da `'I' = 'ı'` sayılır, İngilizce kelime aramalarını bozar. Kritik kolonlarda (Email, Lemma, Slug, Settings.Key) collation ayrıca açıkça yazıldı, DB başka collation'la açılsa bile doğru çalışır |
| Filtered index'ler | Sadece ilgili satırları içerir, küçük ve hızlı. **Not:** bağlantıda `QUOTED_IDENTIFIER ON` olmalı (SqlClient varsayılanı zaten ON, sqlcmd'de `-I` parametresi gerekir) |

## 2. Şemalar

| Şema | İçerik |
|---|---|
| `auth` | Kullanıcı, rol, refresh token |
| `content` | Senaryo, kelime, cümle (herkes için ortak içerik) |
| `study` | Kullanıcıya özel: ayarlar, FSRS kartları, tekrar kayıtları, istatistik |
| `ai` | Konuşmalar, yazma çalışmaları, token kullanımı, cache |
| `app` | Sistem ayarları, loglar, mail logları, audit |

## 3. Tablolar

### auth
| Tablo | Açıklama |
|---|---|
| `Roles` | 1 Admin, 2 User |
| `Users` | E-posta (silinmemişler arasında tekil), şifre hash'i, rol, **günlük token limiti** (NULL = sınırsız), aktif/silindi, `MustChangePassword`, `SecurityStamp` (şifre değişince yenilenir → eski token'lar geçersiz), hatalı login sayacı ve kilit, `RowVer` (eşzamanlı düzenleme kontrolü) |
| `RefreshTokens` | Token'ın **SHA-256 hash'i**, `FamilyId` (aynı login zinciri; çalıntı token tekrar kullanılırsa tüm zincir iptal), iptal nedeni |

### study
| Tablo | Açıklama |
|---|---|
| `UserSettings` | Users ile 1:1. Günlük yeni kelime / tekrar limiti, FSRS hedef hatırlama oranı (0.90), hatırlatma (açık/kapalı + saat), haftalık rapor (açık/kapalı + gün), son mail tarihleri (aynı gün 2 kez gitmesin), ses ayarları |
| `Cards` | **FSRS kartı.** Kullanıcı bir içeriği ilk kez çalışınca oluşur. Kart tipi + kelime/cümle + FSRS durumu (State, Stability, Difficulty, Step, Reps, Lapses, DueAt). `IsLeech` = zorlandıklarım |
| `ReviewLogs` | Her cevap: puan (1-4), önceki durum, sonraki stability/difficulty, süre. İleride FSRS parametrelerini kişiye göre optimize etmek için gerekli |
| `UserKnownWords` | Seviye testinde "biliyorum" denilen kelimeler (kart oluşmaz) |
| `UserScenarioProgress` | Senaryo başlama/bitirme |
| `DailyStats` | **Günlük özet.** Panel, ısı haritası, seri ve token limiti kontrolü tek satırdan okunur. Her cevapta upsert edilir |

### content
| Tablo | Açıklama |
|---|---|
| `ScenarioCategories` | Seyahat, Otel, Yeme-İçme, Ulaşım, Alışveriş, Sağlık... (11 kategori) |
| `Scenarios` | Senaryo: başlık TR/EN, seviye, durum (Taslak / Yayında / Arşiv), AI ile mi üretildi |
| `Words` | Kelime **ve kalıplar** ("check in", "could I get"). Lemma + tür tekil. NGSL sıklık sırası, seviye, telaffuz, Türkçe anlam, kullanım notu |
| `Sentences` | Senaryo diyalog satırları (sıra + konuşan rol) ve kelime örnek cümleleri. EN + TR |
| `SentenceWords` | Hangi cümlede hangi kelime, **karakter konumuyla** (boşluk doldurma ve vurgulama için) |
| `ScenarioWords` | Senaryonun hedef kelimeleri |

### ai
| Tablo | Açıklama |
|---|---|
| `Conversations` / `Messages` | AI rol yapma konuşmaları. Kullanıcı mesajına düzeltme JSON'u eklenir |
| `WritingSubmissions` | Yazma çalışması: kullanıcı metni, düzeltilmiş metin, geri bildirim, puan |
| `Usage` | Her LLM çağrısı: sağlayıcı, model, özellik, token, başarı, yedeğe geçildi mi, süre |
| `ResponseCache` | Ortak içerik cache'i (istek hash'i → yanıt). Aynı istek tekrar LLM'e gitmez |

### app
| Tablo | Açıklama |
|---|---|
| `Settings` | Dinamik ayarlar (anahtar-değer). Gemini anahtarı, SMTP, cron anahtarı. `IsEncrypted = 1` olanlar AES-GCM ile şifreli |
| `Logs` | Serilog SQL tablosu (Warning ve üstü). Correlation ID, kullanıcı, istek yolu, IP |
| `EmailLogs` | Gönderilen her mail: tip, alıcı, başarılı mı, hata |
| `AuditLogs` | Admin işlemleri: kim, ne yaptı, eski/yeni değer |

## 4. Enum Karşılıkları (C#)

| Kolon | Değerler |
|---|---|
| `Users.RoleId` | 1 Admin, 2 User |
| `*.Level` | 1 A1, 2 A2, 3 B1, 4 B2, 5 C1, 6 C2 |
| `*.Status` (içerik) | 0 Draft, 1 Published, 2 Archived |
| `Words.PartOfSpeech` | 1 Noun, 2 Verb, 3 Adjective, 4 Adverb, 5 Pronoun, 6 Preposition, 7 Conjunction, 8 Determiner, 9 Interjection, 10 Phrase, 11 PhrasalVerb, 12 Other |
| `Cards.CardType` | 1 WordMeaning (kelimeyi gör → anlamı hatırla), 2 WordListening (dinle → anlamı hatırla), 3 SentenceListening (cümleyi dinle → anla), 4 SentenceCloze (boşluk doldur), 5 Production (TR → EN, ileride) |
| `Cards.State` | 0 New, 1 Learning, 2 Review, 3 Relearning |
| `ReviewLogs.Rating` | 1 Again (bilemedim), 2 Hard (zor), 3 Good (bildim), 4 Easy (çok kolay) |
| `UserKnownWords.Source` | 1 PlacementTest, 2 Manual |
| `RefreshTokens.RevokeReason` | 1 Rotated, 2 Logout, 3 PasswordChanged, 4 ReuseDetected, 5 AdminAction |
| `Conversations.Mode` | 1 RolePlay, 2 FreeTalk |
| `Messages.Role` | 1 System, 2 User, 3 Assistant |
| `Usage.Provider` | 1 Gemini, 2 OpenRouter, 3 Groq |
| `Usage.Feature` / `ResponseCache.Feature` | 1 RolePlay, 2 Writing, 3 WordExplain, 4 ContentGeneration, 5 Other |
| `EmailLogs.MailType` | 1 DailyReminder, 2 WeeklyReport, 3 Test, 4 AccountInfo |
| `EmailLogs.Status` | 1 Sent, 2 Failed |
| `UserSettings.WeeklyReportDay` | 1 Pazartesi ... 7 Pazar |

## 5. Kritik Sorgular ve Index'leri

| Sorgu | Index |
|---|---|
| Bugün tekrar edilecek kartlar (`UserId = @u AND DueAt <= @now AND IsSuspended = 0`) | `IX_Cards_Due` (filtered, covering → tabloya hiç gitmez) |
| Zorlandıklarım listesi | `IX_Cards_Leech` (filtered) |
| Yeni kelime seçimi (senaryodaki, kartı olmayan, bilinmeyen kelimeler) | `PK_ScenarioWords` + `IX_Cards_User_Word` + `PK_UserKnownWords` |
| Panel / ısı haritası / seri | `PK_DailyStats` (UserId, StatDate) |
| Token limiti kontrolü | `PK_DailyStats` → tek satır okuma |
| Login | `UX_Users_Email` (filtered) |
| Refresh token doğrulama | `UX_RefreshTokens_TokenHash` |
| Log arama | `IX_Logs_TimeStamp`, `IX_Logs_Correlation` |

## 6. Bilinçli Kararlar

- **Kart tembel oluşur:** Kullanıcı bir kelimeyi ilk çalıştığında kart açılır. 3000 kelime için baştan 3000 kart açılmaz, tablo küçük kalır.
- **`DailyStats` özet tablosu:** Panel her açıldığında `ReviewLogs` taranmaz. Her cevapta tek satır upsert edilir.
- **`ReviewLogs.UserId` tekrar tutuluyor:** İstatistik sorgularında `Cards` ile join gerekmesin diye.
- **FSRS değerleri `real` (4 byte):** Algoritma için yeterli hassasiyet.
- **İlk admin SQL'de değil:** Şifre hash'i .NET'te üretiliyor. API ilk açılışta hiç admin yoksa `appsettings.json`'daki bilgilerle oluşturur.
- **Eski kayıt temizliği:** `app.Logs` 90 gün, `ai.Usage` 180 gün (cron işiyle).

## 7. Faz 3'te Eklenecek Tablolar

- `content.ReadingTexts`: okuma metinleri (başlık, metin, seviye, kelime sayısı, senaryo)
- `content.MediaRecommendations`: dizi / film / YouTube / podcast önerileri (tip, URL, seviye, açıklama)
- `study.UserReadingProgress`: okunan metinler
