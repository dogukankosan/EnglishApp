/* =============================================================
   EnglishApp - Dikte, hedef, AI koç, okuma, öneriler, video. Tekrar çalıştırmak güvenli.
   ============================================================= */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

-- ---------- Dikte sonuçları ----------
IF OBJECT_ID('study.DictationAttempts') IS NULL
CREATE TABLE study.DictationAttempts
(
    Id              bigint         IDENTITY(1,1) NOT NULL CONSTRAINT PK_DictationAttempts PRIMARY KEY,
    UserId          int            NOT NULL CONSTRAINT FK_DictationAttempts_Users REFERENCES auth.Users(Id),
    SentenceId      int            NOT NULL CONSTRAINT FK_DictationAttempts_Sentences REFERENCES content.Sentences(Id) ON DELETE CASCADE,
    ScorePercent    tinyint        NOT NULL,
    TypedText       nvarchar(500)  NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_DictationAttempts_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time'))
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DictationAttempts_User')
    CREATE INDEX IX_DictationAttempts_User ON study.DictationAttempts(UserId, SentenceId, CreatedAt DESC) INCLUDE (ScorePercent);
GO

-- ---------- Hedef + seri koruma ----------
IF COL_LENGTH('study.UserSettings', 'TargetLevel') IS NULL
    ALTER TABLE study.UserSettings ADD
        TargetLevel  tinyint NULL,       -- CEFR 1..6
        TargetDate   date    NULL,
        StreakFreeze bit     NOT NULL CONSTRAINT DF_UserSettings_StreakFreeze DEFAULT (1);  -- haftada 1 dinlenme günü
GO

-- ---------- Haftalık AI koç raporu ----------
IF OBJECT_ID('ai.CoachReports') IS NULL
CREATE TABLE ai.CoachReports
(
    Id              bigint         IDENTITY(1,1) NOT NULL CONSTRAINT PK_CoachReports PRIMARY KEY,
    UserId          int            NOT NULL CONSTRAINT FK_CoachReports_Users REFERENCES auth.Users(Id),
    WeekStart       date           NOT NULL,
    ContentJson     nvarchar(max)  NOT NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_CoachReports_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time')),
    CONSTRAINT UQ_CoachReports_User_Week UNIQUE (UserId, WeekStart)
);
GO

-- ---------- Okuma metinleri (kullanıcıya özel, AI ile) ----------
IF OBJECT_ID('content.ReadingTexts') IS NULL
CREATE TABLE content.ReadingTexts
(
    Id              bigint         IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReadingTexts PRIMARY KEY,
    UserId          int            NOT NULL CONSTRAINT FK_ReadingTexts_Users REFERENCES auth.Users(Id),
    Title           nvarchar(200)  NOT NULL,
    Level           tinyint        NOT NULL,
    Topic           nvarchar(150)  NULL,
    ContentJson     nvarchar(max)  NOT NULL,
    WordCount       smallint       NOT NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_ReadingTexts_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time')),
    ReadAt          datetime2(0)   NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ReadingTexts_User')
    CREATE INDEX IX_ReadingTexts_User ON content.ReadingTexts(UserId, CreatedAt DESC);
GO

-- ---------- Dizi / video / podcast önerileri ----------
IF OBJECT_ID('content.MediaRecommendations') IS NULL
CREATE TABLE content.MediaRecommendations
(
    Id              int            IDENTITY(1,1) NOT NULL CONSTRAINT PK_MediaRecommendations PRIMARY KEY,
    MediaType       tinyint        NOT NULL,     -- 1 Dizi, 2 YouTube, 3 Podcast, 4 Film
    Title           nvarchar(150)  NOT NULL CONSTRAINT UQ_MediaRecommendations_Title UNIQUE,
    Level           tinyint        NOT NULL,     -- CEFR 1..6 (önerilen en düşük seviye)
    Accent          varchar(5)     NULL,         -- us / uk / mix
    DescriptionTr   nvarchar(600)  NOT NULL,
    TipTr           nvarchar(400)  NULL,
    Url             nvarchar(400)  NOT NULL,
    SortOrder       smallint       NOT NULL CONSTRAINT DF_MediaRecommendations_SortOrder DEFAULT (0),
    IsActive        bit            NOT NULL CONSTRAINT DF_MediaRecommendations_IsActive DEFAULT (1)
);
GO

IF OBJECT_ID('study.UserMedia') IS NULL
CREATE TABLE study.UserMedia
(
    UserId          int            NOT NULL CONSTRAINT FK_UserMedia_Users REFERENCES auth.Users(Id),
    MediaId         int            NOT NULL CONSTRAINT FK_UserMedia_Media REFERENCES content.MediaRecommendations(Id) ON DELETE CASCADE,
    Status          tinyint        NOT NULL,     -- 1 Listemde, 2 İzliyorum/Dinliyorum, 3 Bitirdim
    UpdatedAt       datetime2(0)   NOT NULL,
    CONSTRAINT PK_UserMedia PRIMARY KEY (UserId, MediaId)
);
GO

-- ---------- YouTube sahneleri ----------
IF OBJECT_ID('study.Videos') IS NULL
CREATE TABLE study.Videos
(
    Id              bigint         IDENTITY(1,1) NOT NULL CONSTRAINT PK_Videos PRIMARY KEY,
    UserId          int            NOT NULL CONSTRAINT FK_Videos_Users REFERENCES auth.Users(Id),
    YoutubeId       varchar(20)    NOT NULL,
    Title           nvarchar(200)  NOT NULL,
    SegmentsJson    nvarchar(max)  NOT NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Videos_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time'))
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Videos_User')
    CREATE INDEX IX_Videos_User ON study.Videos(UserId, CreatedAt DESC);
GO

-- ---------- Genel kelime havuzu (senaryo dışı, sıklık sırasına göre) ----------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Words_General')
    CREATE INDEX IX_Words_General ON content.Words(FrequencyRank) INCLUDE (Status, OwnerUserId) WHERE OwnerUserId IS NULL AND FrequencyRank IS NOT NULL;
GO
