/* =============================================================
   EnglishApp - Giriş güvenlik doğrulaması (Cloudflare Turnstile). Tekrar çalıştırmak güvenli.
   Anahtarlar boşsa doğrulama kapalıdır; Admin > Ayarlar > Güvenlik ekranından girilir.
   ============================================================= */
SET NOCOUNT ON;
GO

MERGE app.Settings AS t
USING (VALUES
    ('Security.Turnstile.SiteKey',   NULL, 0, 'Security', N'Cloudflare Turnstile site anahtarı (herkese açık). 3 hatalı girişten sonra güvenlik doğrulaması çıkar'),
    ('Security.Turnstile.SecretKey', NULL, 1, 'Security', N'Cloudflare Turnstile gizli anahtarı')
) AS s ([Key], Value, IsEncrypted, GroupName, DescriptionTr)
ON t.[Key] = s.[Key]
WHEN NOT MATCHED THEN INSERT ([Key], Value, IsEncrypted, GroupName, DescriptionTr)
                      VALUES (s.[Key], s.Value, s.IsEncrypted, s.GroupName, s.DescriptionTr);
GO