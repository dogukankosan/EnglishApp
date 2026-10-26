/* =============================================================
   EnglishApp - Mail HTML imzası. Tekrar çalıştırmak güvenli.
   ============================================================= */
SET NOCOUNT ON;
GO

MERGE app.Settings AS t
USING (VALUES
    ('Smtp.SignatureHtml',
     N'<p style="margin:0;color:#6b7280;font-size:13px">EnglishApp · Her gün biraz, gerçek hayat için İngilizce</p><p style="margin:4px 0 0;color:#9ca3af;font-size:12px">Geliştirici: <a href="https://github.com/dogukankosan" style="color:#4f46e5;text-decoration:none">Doğukan Koşan</a> · Developer</p>',
     0, 'Smtp', N'Tüm maillerin altına eklenen HTML imza')
) AS s ([Key], Value, IsEncrypted, GroupName, DescriptionTr)
ON t.[Key] = s.[Key]
WHEN NOT MATCHED THEN INSERT ([Key], Value, IsEncrypted, GroupName, DescriptionTr)
                      VALUES (s.[Key], s.Value, s.IsEncrypted, s.GroupName, s.DescriptionTr);
GO
