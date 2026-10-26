/* =============================================================
   EnglishApp - Sunucu tarafı ses tanıma (Groq Whisper). Tekrar çalıştırmak güvenli.
   ============================================================= */
SET NOCOUNT ON;
GO

MERGE app.Settings AS t
USING (VALUES
    ('Llm.Groq.SttModel', N'whisper-large-v3-turbo', 0, 'Llm', N'Ses tanıma modeli (Groq Whisper). Tarayıcı tanıyamazsa (Brave/Firefox) bu kullanılır')
) AS s ([Key], Value, IsEncrypted, GroupName, DescriptionTr)
ON t.[Key] = s.[Key]
WHEN NOT MATCHED THEN INSERT ([Key], Value, IsEncrypted, GroupName, DescriptionTr)
                      VALUES (s.[Key], s.Value, s.IsEncrypted, s.GroupName, s.DescriptionTr);
GO
