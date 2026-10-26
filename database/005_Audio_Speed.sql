/* =============================================================
   EnglishApp - Gemini TTS ve hızlı sohbet ayarları. Tekrar çalıştırmak güvenli.
   ============================================================= */
SET NOCOUNT ON;
GO

MERGE app.Settings AS t
USING (VALUES
    ('Llm.Gemini.ChatReasoningEffort', N'minimal', 0, 'Llm', N'Sohbette düşünme seviyesi (hız için minimal). Boş = model varsayılanı'),
    ('Llm.Gemini.TtsEnabled',  N'true', 0, 'Llm', N'AI cevaplarını Gemini doğal sesiyle oku (true/false). Kota dolarsa tarayıcı sesi kullanılır'),
    ('Llm.Gemini.TtsModels',   N'gemini-3.8-flash-lite-tts,gemini-3.1-flash-tts-preview', 0, 'Llm', N'Gemini TTS modelleri, deneme sırasıyla (virgülle)'),
    ('Llm.Gemini.TtsVoices',   N'Kore,Puck,Aoede,Charon,Achird,Sulafat', 0, 'Llm', N'AI rollerine dağıtılan Gemini sesleri (virgülle)')
) AS s ([Key], Value, IsEncrypted, GroupName, DescriptionTr)
ON t.[Key] = s.[Key]
WHEN NOT MATCHED THEN INSERT ([Key], Value, IsEncrypted, GroupName, DescriptionTr)
                      VALUES (s.[Key], s.Value, s.IsEncrypted, s.GroupName, s.DescriptionTr);
GO
