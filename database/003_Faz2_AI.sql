/* =============================================================
   EnglishApp - Faz 2 (AI rol yapma + yazma)
   Tekrar çalıştırmak güvenli.
   ============================================================= */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

-- Konuşmada AI'ın ve kullanıcının rolü + bitişte üretilen özet
IF COL_LENGTH('ai.Conversations', 'AiRole') IS NULL
    ALTER TABLE ai.Conversations ADD AiRole nvarchar(50) NULL, UserRole nvarchar(50) NULL, SummaryJson nvarchar(max) NULL;
GO

-- Yazma görevi kodu (sabit görev listesinden) ve tahmini seviye
IF COL_LENGTH('ai.WritingSubmissions', 'TaskCode') IS NULL
    ALTER TABLE ai.WritingSubmissions ADD TaskCode varchar(50) NULL, EstimatedLevel varchar(5) NULL;
GO

MERGE app.Settings AS t
USING (VALUES
    ('Llm.Gemini.ReasoningEffort', N'low', 0, 'Llm', N'Gemini düşünme seviyesi (none/minimal/low/medium/high). Düşük = hızlı yanıt, az token. Boş = model varsayılanı'),
    ('Llm.MaxMessagesPerConversation', N'40', 0, 'Llm', N'Bir konuşmadaki en fazla kullanıcı mesajı (maliyet kontrolü)')
) AS s ([Key], Value, IsEncrypted, GroupName, DescriptionTr)
ON t.[Key] = s.[Key]
WHEN NOT MATCHED THEN INSERT ([Key], Value, IsEncrypted, GroupName, DescriptionTr)
                      VALUES (s.[Key], s.Value, s.IsEncrypted, s.GroupName, s.DescriptionTr);
GO

-- Lite model için doğrulanmış güncel model adı (admin panelinden değiştirilebilir)
UPDATE app.Settings SET Value = N'gemini-3.5-flash-lite'
WHERE [Key] = 'Llm.Gemini.LiteModel' AND Value = N'gemini-flash-lite-latest';
GO
