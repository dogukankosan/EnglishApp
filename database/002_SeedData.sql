/* =============================================================
   EnglishApp - Başlangıç verileri
   İlk admin kullanıcısı burada DEĞİL: şifre hash'i .NET'te üretildiği için
   API ilk açılışta appsettings.json'daki bilgilerle oluşturur.
   ============================================================= */
SET NOCOUNT ON;
GO

MERGE auth.Roles AS t
USING (VALUES (1, 'Admin'), (2, 'User')) AS s (Id, Name)
ON t.Id = s.Id
WHEN MATCHED THEN UPDATE SET Name = s.Name
WHEN NOT MATCHED THEN INSERT (Id, Name) VALUES (s.Id, s.Name);
GO

MERGE content.ScenarioCategories AS t
USING (VALUES
    (1,  N'Seyahat & Havalimanı', N'Travel & Airport',      'bi-airplane',       1),
    (2,  N'Otel & Konaklama',     N'Hotel & Accommodation', 'bi-building',       2),
    (3,  N'Yeme & İçme',          N'Food & Drink',          'bi-cup-hot',        3),
    (4,  N'Ulaşım',               N'Transportation',        'bi-taxi-front',     4),
    (5,  N'Alışveriş',            N'Shopping',              'bi-bag',            5),
    (6,  N'Sağlık',               N'Health',                'bi-heart-pulse',    6),
    (7,  N'Yol Sorma & Şehir',    N'Directions & City',     'bi-signpost',       7),
    (8,  N'Tanışma & Sohbet',     N'Small Talk',            'bi-chat-dots',      8),
    (9,  N'Acil Durumlar',        N'Emergencies',           'bi-exclamation-triangle', 9),
    (10, N'Telefon & İletişim',   N'Phone & Communication', 'bi-telephone',      10),
    (11, N'İş Hayatı',            N'Work',                  'bi-briefcase',      11)
) AS s (Id, NameTr, NameEn, IconName, SortOrder)
ON t.Id = s.Id
WHEN MATCHED THEN UPDATE SET NameTr = s.NameTr, NameEn = s.NameEn, IconName = s.IconName, SortOrder = s.SortOrder
WHEN NOT MATCHED THEN INSERT (Id, NameTr, NameEn, IconName, SortOrder) VALUES (s.Id, s.NameTr, s.NameEn, s.IconName, s.SortOrder);
GO

-- Ayar anahtarları (değerler admin panelinden girilir, hassas olanlar şifreli kaydedilir)
MERGE app.Settings AS t
USING (VALUES
    ('Llm.Gemini.ApiKey',        CAST(NULL AS nvarchar(max)), 1, 'Llm',     N'Gemini API anahtarı'),
    ('Llm.Gemini.Model',         N'gemini-3.6-flash',         0, 'Llm',     N'Ana model'),
    ('Llm.Gemini.LiteModel',     N'gemini-3.5-flash-lite', 0, 'Llm',     N'Hafif işler için model'),
    ('Llm.OpenRouter.ApiKey',    NULL,                        1, 'Llm',     N'OpenRouter API anahtarı (yedek)'),
    ('Llm.OpenRouter.Model',     NULL,                        0, 'Llm',     N'OpenRouter ücretsiz model adı (:free)'),
    ('Llm.Groq.ApiKey',          NULL,                        1, 'Llm',     N'Groq API anahtarı (yedek)'),
    ('Llm.Groq.Model',           NULL,                        0, 'Llm',     N'Groq model adı'),
    ('Smtp.Host',                NULL,                        0, 'Smtp',    N'SMTP sunucu'),
    ('Smtp.Port',                N'587',                      0, 'Smtp',    N'SMTP port'),
    ('Smtp.UseSsl',              N'true',                     0, 'Smtp',    N'SSL/TLS kullan'),
    ('Smtp.UserName',            NULL,                        0, 'Smtp',    N'SMTP kullanıcı adı'),
    ('Smtp.Password',            NULL,                        1, 'Smtp',    N'SMTP şifresi'),
    ('Smtp.FromAddress',         NULL,                        0, 'Smtp',    N'Gönderen adresi'),
    ('Smtp.FromName',            N'EnglishApp',               0, 'Smtp',    N'Gönderen adı'),
    ('Jobs.SecretKey',           NULL,                        1, 'Jobs',    N'Cron tetikleme gizli anahtarı'),
    ('General.AppUrl',           NULL,                        0, 'General', N'Uygulamanın web adresi (mail linkleri için)'),
    ('General.DefaultDailyTokenLimit', N'50000',              0, 'General', N'Yeni kullanıcıya varsayılan günlük token limiti')
) AS s ([Key], Value, IsEncrypted, GroupName, DescriptionTr)
ON t.[Key] = s.[Key]
WHEN NOT MATCHED THEN INSERT ([Key], Value, IsEncrypted, GroupName, DescriptionTr)
                      VALUES (s.[Key], s.Value, s.IsEncrypted, s.GroupName, s.DescriptionTr);
GO
