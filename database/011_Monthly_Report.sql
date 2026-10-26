/* =============================================================
   EnglishApp - Aylık rapor maili (kullanıcı başına açılıp kapatılır). Tekrar çalıştırmak güvenli.
   ============================================================= */
SET NOCOUNT ON;
GO
IF COL_LENGTH('study.UserSettings', 'MonthlyReportEnabled') IS NULL
    ALTER TABLE study.UserSettings ADD
        MonthlyReportEnabled       bit  NOT NULL CONSTRAINT DF_UserSettings_MonthlyReportEnabled DEFAULT (0),
        LastMonthlyReportSentDate  date NULL;
GO