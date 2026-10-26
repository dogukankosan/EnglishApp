/* =============================================================
   EnglishApp - Dinleme testi, cümle yakalama, seyahat kiti. Tekrar çalıştırmak güvenli.
   ============================================================= */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

-- Kullanıcının kendi eklediği (cümle yakalama / seyahat kiti) içerik: NULL = herkese ortak içerik
IF COL_LENGTH('content.Words', 'OwnerUserId') IS NULL
    ALTER TABLE content.Words ADD OwnerUserId int NULL;
IF COL_LENGTH('content.Sentences', 'OwnerUserId') IS NULL
    ALTER TABLE content.Sentences ADD OwnerUserId int NULL, SourceNote nvarchar(200) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Sentences_Owner')
    CREATE INDEX IX_Sentences_Owner ON content.Sentences(OwnerUserId, Id DESC) WHERE OwnerUserId IS NOT NULL;
GO

-- Dinleme testi soruları: senaryo başına bir kez AI ile üretilir, herkes kullanır
IF OBJECT_ID('content.ListeningQuizzes') IS NULL
CREATE TABLE content.ListeningQuizzes
(
    ScenarioId      int            NOT NULL CONSTRAINT PK_ListeningQuizzes PRIMARY KEY
                                   CONSTRAINT FK_ListeningQuizzes_Scenarios REFERENCES content.Scenarios(Id) ON DELETE CASCADE,
    QuestionsJson   nvarchar(max)  NOT NULL,
    Model           varchar(100)   NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_ListeningQuizzes_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time'))
);
GO

-- Dinleme testi sonuçları (aylık seviye ölçümü buradan)
IF OBJECT_ID('study.QuizAttempts') IS NULL
CREATE TABLE study.QuizAttempts
(
    Id              bigint        IDENTITY(1,1) NOT NULL CONSTRAINT PK_QuizAttempts PRIMARY KEY,
    UserId          int           NOT NULL CONSTRAINT FK_QuizAttempts_Users REFERENCES auth.Users(Id),
    ScenarioId      int           NOT NULL CONSTRAINT FK_QuizAttempts_Scenarios REFERENCES content.Scenarios(Id),
    CorrectCount    tinyint       NOT NULL,
    TotalCount      tinyint       NOT NULL,
    ReplayCount     tinyint       NOT NULL CONSTRAINT DF_QuizAttempts_ReplayCount DEFAULT (0),
    Accent          varchar(5)    NULL,
    CreatedAt       datetime2(0)  NOT NULL CONSTRAINT DF_QuizAttempts_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time')),
    CONSTRAINT CK_QuizAttempts_Counts CHECK (CorrectCount <= TotalCount AND TotalCount > 0)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_QuizAttempts_User')
    CREATE INDEX IX_QuizAttempts_User ON study.QuizAttempts(UserId, CreatedAt DESC) INCLUDE (CorrectCount, TotalCount, ScenarioId);
GO

-- Seyahat kitleri
IF OBJECT_ID('ai.TravelKits') IS NULL
CREATE TABLE ai.TravelKits
(
    Id              bigint         IDENTITY(1,1) NOT NULL CONSTRAINT PK_TravelKits PRIMARY KEY,
    UserId          int            NOT NULL CONSTRAINT FK_TravelKits_Users REFERENCES auth.Users(Id),
    Title           nvarchar(150)  NOT NULL,
    Country         nvarchar(80)   NOT NULL,
    City            nvarchar(80)   NULL,
    Purpose         nvarchar(80)   NULL,
    Days            tinyint        NULL,
    ContentJson     nvarchar(max)  NOT NULL,
    AddedToCards    bit            NOT NULL CONSTRAINT DF_TravelKits_AddedToCards DEFAULT (0),
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_TravelKits_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time'))
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_TravelKits_User')
    CREATE INDEX IX_TravelKits_User ON ai.TravelKits(UserId, CreatedAt DESC);
GO
