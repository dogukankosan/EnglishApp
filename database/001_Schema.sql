/* =============================================================
   EnglishApp - Şema
   Şemalar:  auth (kimlik), content (içerik), study (çalışma/FSRS),
             ai (yapay zeka), app (sistem: ayar, log, mail)
   Tarih kolonları: datetime2(0), HER ZAMAN Türkiye saati.
   Enum kolonları: tinyint (C# enum karşılıkları 02_DB_TASARIMI.md'de)
   ============================================================= */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

IF SCHEMA_ID(N'auth')    IS NULL EXEC (N'CREATE SCHEMA auth');
IF SCHEMA_ID(N'content') IS NULL EXEC (N'CREATE SCHEMA content');
IF SCHEMA_ID(N'study')   IS NULL EXEC (N'CREATE SCHEMA study');
IF SCHEMA_ID(N'ai')      IS NULL EXEC (N'CREATE SCHEMA ai');
IF SCHEMA_ID(N'app')     IS NULL EXEC (N'CREATE SCHEMA app');
GO

/* =============================================================
   AUTH
   ============================================================= */
CREATE TABLE auth.Roles
(
    Id      tinyint      NOT NULL CONSTRAINT PK_Roles PRIMARY KEY,
    Name    varchar(20)  NOT NULL CONSTRAINT UQ_Roles_Name UNIQUE
);
GO

CREATE TABLE auth.Users
(
    Id                  int              IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
    Email               varchar(256)     COLLATE Latin1_General_100_CI_AS NOT NULL,
    PasswordHash        varchar(256)     NOT NULL,
    FullName            nvarchar(100)    NOT NULL,
    ProfileImagePath    nvarchar(260)    NULL,
    RoleId              tinyint          NOT NULL CONSTRAINT FK_Users_Roles REFERENCES auth.Roles(Id),
    DailyTokenLimit     int              NULL,          -- NULL = sınırsız (admin ayarlar)
    IsActive            bit              NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT (1),
    IsDeleted           bit              NOT NULL CONSTRAINT DF_Users_IsDeleted DEFAULT (0),
    MustChangePassword  bit              NOT NULL CONSTRAINT DF_Users_MustChangePassword DEFAULT (1),
    SecurityStamp       uniqueidentifier NOT NULL CONSTRAINT DF_Users_SecurityStamp DEFAULT (NEWID()),
    FailedLoginCount    tinyint          NOT NULL CONSTRAINT DF_Users_FailedLoginCount DEFAULT (0),
    LockoutEndAt        datetime2(0)     NULL,
    LastLoginAt         datetime2(0)     NULL,
    CreatedAt           datetime2(0)     NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time')),
    CreatedBy           int              NULL,
    UpdatedAt           datetime2(0)     NULL,
    UpdatedBy           int              NULL,
    DeletedAt           datetime2(0)     NULL,
    DeletedBy           int              NULL,
    RowVer              rowversion       NOT NULL,
    CONSTRAINT CK_Users_DailyTokenLimit CHECK (DailyTokenLimit IS NULL OR DailyTokenLimit >= 0)
);
GO
-- Silinmemiş kullanıcılar arasında e-posta tekil (silinen kullanıcının e-postası tekrar kullanılabilir)
CREATE UNIQUE INDEX UX_Users_Email ON auth.Users(Email) WHERE IsDeleted = 0;
GO

CREATE TABLE auth.RefreshTokens
(
    Id                  bigint        IDENTITY(1,1) NOT NULL CONSTRAINT PK_RefreshTokens PRIMARY KEY,
    UserId              int           NOT NULL CONSTRAINT FK_RefreshTokens_Users REFERENCES auth.Users(Id),
    TokenHash           binary(32)    NOT NULL,      -- SHA-256, düz token DB'de tutulmaz
    FamilyId            uniqueidentifier NOT NULL,   -- aynı login'den türeyen token zinciri (çalıntı tespiti)
    ExpiresAt           datetime2(0)  NOT NULL,
    CreatedAt           datetime2(0)  NOT NULL CONSTRAINT DF_RefreshTokens_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time')),
    CreatedByIp         varchar(45)   NULL,
    UserAgent           nvarchar(256) NULL,
    RevokedAt           datetime2(0)  NULL,
    RevokeReason        tinyint       NULL,          -- 1 Rotated, 2 Logout, 3 PasswordChanged, 4 ReuseDetected, 5 AdminAction
    ReplacedByTokenId   bigint        NULL
);
GO
CREATE UNIQUE INDEX UX_RefreshTokens_TokenHash ON auth.RefreshTokens(TokenHash);
CREATE INDEX IX_RefreshTokens_UserId ON auth.RefreshTokens(UserId) INCLUDE (FamilyId, RevokedAt, ExpiresAt);
GO

/* =============================================================
   STUDY - kullanıcı ayarları (Users ile 1:1)
   ============================================================= */
CREATE TABLE study.UserSettings
(
    UserId                   int           NOT NULL CONSTRAINT PK_UserSettings PRIMARY KEY
                                           CONSTRAINT FK_UserSettings_Users REFERENCES auth.Users(Id),
    DailyNewWordLimit        smallint      NOT NULL CONSTRAINT DF_UserSettings_DailyNewWordLimit DEFAULT (15),
    DailyReviewLimit         smallint      NOT NULL CONSTRAINT DF_UserSettings_DailyReviewLimit DEFAULT (200),
    DesiredRetention         decimal(3,2)  NOT NULL CONSTRAINT DF_UserSettings_DesiredRetention DEFAULT (0.90),  -- FSRS hedef hatırlama oranı
    ReminderEnabled          bit           NOT NULL CONSTRAINT DF_UserSettings_ReminderEnabled DEFAULT (0),
    ReminderTime             time(0)       NOT NULL CONSTRAINT DF_UserSettings_ReminderTime DEFAULT ('20:00'),
    WeeklyReportEnabled      bit           NOT NULL CONSTRAINT DF_UserSettings_WeeklyReportEnabled DEFAULT (0),
    WeeklyReportDay          tinyint       NOT NULL CONSTRAINT DF_UserSettings_WeeklyReportDay DEFAULT (7),     -- 1=Pzt ... 7=Paz
    LastReminderSentDate     date          NULL,
    LastWeeklyReportSentDate date          NULL,
    TtsVoice                 nvarchar(100) NULL,
    TtsRate                  decimal(3,2)  NOT NULL CONSTRAINT DF_UserSettings_TtsRate DEFAULT (1.00),
    PlacementTestDone        bit           NOT NULL CONSTRAINT DF_UserSettings_PlacementTestDone DEFAULT (0),
    UpdatedAt                datetime2(0)  NULL,
    CONSTRAINT CK_UserSettings_DailyNewWordLimit CHECK (DailyNewWordLimit BETWEEN 0 AND 200),
    CONSTRAINT CK_UserSettings_DailyReviewLimit  CHECK (DailyReviewLimit BETWEEN 0 AND 2000),
    CONSTRAINT CK_UserSettings_DesiredRetention  CHECK (DesiredRetention BETWEEN 0.70 AND 0.99),
    CONSTRAINT CK_UserSettings_WeeklyReportDay   CHECK (WeeklyReportDay BETWEEN 1 AND 7),
    CONSTRAINT CK_UserSettings_TtsRate           CHECK (TtsRate BETWEEN 0.50 AND 2.00)
);
GO

/* =============================================================
   CONTENT
   ============================================================= */
CREATE TABLE content.ScenarioCategories
(
    Id          tinyint       NOT NULL CONSTRAINT PK_ScenarioCategories PRIMARY KEY,
    NameTr      nvarchar(50)  NOT NULL,
    NameEn      nvarchar(50)  NOT NULL,
    IconName    varchar(50)   NULL,
    SortOrder   tinyint       NOT NULL
);
GO

CREATE TABLE content.Scenarios
(
    Id              int            IDENTITY(1,1) NOT NULL CONSTRAINT PK_Scenarios PRIMARY KEY,
    CategoryId      tinyint        NOT NULL CONSTRAINT FK_Scenarios_Categories REFERENCES content.ScenarioCategories(Id),
    Slug            varchar(100)   COLLATE Latin1_General_100_CI_AS NOT NULL CONSTRAINT UQ_Scenarios_Slug UNIQUE,
    TitleTr         nvarchar(150)  NOT NULL,
    TitleEn         nvarchar(150)  NOT NULL,
    DescriptionTr   nvarchar(500)  NULL,
    Level           tinyint        NOT NULL,     -- CEFR: 1=A1 2=A2 3=B1 4=B2 5=C1 6=C2
    SortOrder       smallint       NOT NULL CONSTRAINT DF_Scenarios_SortOrder DEFAULT (0),
    Status          tinyint        NOT NULL CONSTRAINT DF_Scenarios_Status DEFAULT (0),  -- 0 Draft, 1 Published, 2 Archived
    IsAiGenerated   bit            NOT NULL CONSTRAINT DF_Scenarios_IsAiGenerated DEFAULT (0),
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Scenarios_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time')),
    CreatedBy       int            NULL,
    UpdatedAt       datetime2(0)   NULL,
    UpdatedBy       int            NULL,
    PublishedAt     datetime2(0)   NULL,
    PublishedBy     int            NULL,
    CONSTRAINT CK_Scenarios_Level  CHECK (Level BETWEEN 1 AND 6),
    CONSTRAINT CK_Scenarios_Status CHECK (Status BETWEEN 0 AND 2)
);
GO
CREATE INDEX IX_Scenarios_Status_Sort ON content.Scenarios(Status, CategoryId, SortOrder) INCLUDE (Slug, TitleTr, TitleEn, Level);
GO

-- Kelimeler VE kalıplar ("check in", "could I get") aynı tabloda
CREATE TABLE content.Words
(
    Id              int            IDENTITY(1,1) NOT NULL CONSTRAINT PK_Words PRIMARY KEY,
    Lemma           nvarchar(100)  COLLATE Latin1_General_100_CI_AS NOT NULL,
    PartOfSpeech    tinyint        NOT NULL,     -- 1 Noun 2 Verb 3 Adj 4 Adv 5 Pron 6 Prep 7 Conj 8 Det 9 Interj 10 Phrase 11 PhrasalVerb 12 Other
    FrequencyRank   int            NULL,         -- NGSL sırası (1 = en sık)
    Level           tinyint        NOT NULL,     -- CEFR 1..6
    Ipa             nvarchar(100)  NULL,         -- telaffuz (US)
    MeaningTr       nvarchar(300)  NOT NULL,
    DefinitionEn    nvarchar(500)  NULL,
    UsageNoteTr     nvarchar(500)  NULL,
    AudioPath       nvarchar(260)  NULL,
    Status          tinyint        NOT NULL CONSTRAINT DF_Words_Status DEFAULT (0),  -- 0 Draft, 1 Published, 2 Archived
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Words_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time')),
    UpdatedAt       datetime2(0)   NULL,
    CONSTRAINT UQ_Words_Lemma_Pos UNIQUE (Lemma, PartOfSpeech),
    CONSTRAINT CK_Words_Level  CHECK (Level BETWEEN 1 AND 6),
    CONSTRAINT CK_Words_Status CHECK (Status BETWEEN 0 AND 2)
);
GO
CREATE INDEX IX_Words_Frequency ON content.Words(Status, FrequencyRank) INCLUDE (Lemma, Level);
GO

-- Cümleler: senaryo diyalog satırları + kelime örnek cümleleri
CREATE TABLE content.Sentences
(
    Id              int            IDENTITY(1,1) NOT NULL CONSTRAINT PK_Sentences PRIMARY KEY,
    ScenarioId      int            NULL CONSTRAINT FK_Sentences_Scenarios REFERENCES content.Scenarios(Id),
    LineOrder       smallint       NULL,         -- diyalog içindeki sıra (senaryo cümlesi ise)
    SpeakerRole     nvarchar(50)   NULL,         -- "Receptionist", "Guest" ...
    TextEn          nvarchar(500)  NOT NULL,
    TextTr          nvarchar(500)  NOT NULL,
    Level           tinyint        NOT NULL,
    AudioPath       nvarchar(260)  NULL,
    Status          tinyint        NOT NULL CONSTRAINT DF_Sentences_Status DEFAULT (0),
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Sentences_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time')),
    UpdatedAt       datetime2(0)   NULL,
    CONSTRAINT CK_Sentences_Level  CHECK (Level BETWEEN 1 AND 6),
    CONSTRAINT CK_Sentences_Status CHECK (Status BETWEEN 0 AND 2)
);
GO
CREATE INDEX IX_Sentences_Scenario ON content.Sentences(ScenarioId, LineOrder) WHERE ScenarioId IS NOT NULL;
GO

-- Hangi cümlede hangi kelime geçiyor (boşluk doldurma ve vurgulama için konum bilgisiyle)
CREATE TABLE content.SentenceWords
(
    SentenceId      int       NOT NULL CONSTRAINT FK_SentenceWords_Sentences REFERENCES content.Sentences(Id) ON DELETE CASCADE,
    WordId          int       NOT NULL CONSTRAINT FK_SentenceWords_Words REFERENCES content.Words(Id),
    CharStart       smallint  NOT NULL,
    CharLength      smallint  NOT NULL,
    CONSTRAINT PK_SentenceWords PRIMARY KEY (SentenceId, WordId, CharStart)
);
GO
CREATE INDEX IX_SentenceWords_Word ON content.SentenceWords(WordId) INCLUDE (CharStart, CharLength);
GO

-- Senaryonun hedef kelimeleri
CREATE TABLE content.ScenarioWords
(
    ScenarioId  int       NOT NULL CONSTRAINT FK_ScenarioWords_Scenarios REFERENCES content.Scenarios(Id) ON DELETE CASCADE,
    WordId      int       NOT NULL CONSTRAINT FK_ScenarioWords_Words REFERENCES content.Words(Id),
    SortOrder   smallint  NOT NULL,
    CONSTRAINT PK_ScenarioWords PRIMARY KEY (ScenarioId, WordId)
);
GO
CREATE INDEX IX_ScenarioWords_Word ON content.ScenarioWords(WordId);
GO

/* =============================================================
   STUDY - FSRS kartları ve tekrar kayıtları
   ============================================================= */
-- Kart sadece kullanıcı bir kelimeyi/cümleyi ilk kez çalıştığında oluşur
CREATE TABLE study.Cards
(
    Id              bigint        IDENTITY(1,1) NOT NULL CONSTRAINT PK_Cards PRIMARY KEY,
    UserId          int           NOT NULL CONSTRAINT FK_Cards_Users REFERENCES auth.Users(Id),
    CardType        tinyint       NOT NULL,     -- 1 WordMeaning, 2 WordListening, 3 SentenceListening, 4 SentenceCloze, 5 Production
    WordId          int           NULL CONSTRAINT FK_Cards_Words REFERENCES content.Words(Id),
    SentenceId      int           NULL CONSTRAINT FK_Cards_Sentences REFERENCES content.Sentences(Id),
    -- FSRS durumu
    State           tinyint       NOT NULL CONSTRAINT DF_Cards_State DEFAULT (0),  -- 0 New, 1 Learning, 2 Review, 3 Relearning
    Stability       real          NOT NULL CONSTRAINT DF_Cards_Stability DEFAULT (0),
    Difficulty      real          NOT NULL CONSTRAINT DF_Cards_Difficulty DEFAULT (0),
    Step            tinyint       NOT NULL CONSTRAINT DF_Cards_Step DEFAULT (0),
    Reps            int           NOT NULL CONSTRAINT DF_Cards_Reps DEFAULT (0),
    Lapses          int           NOT NULL CONSTRAINT DF_Cards_Lapses DEFAULT (0),
    DueAt           datetime2(0)  NOT NULL,
    LastReviewAt    datetime2(0)  NULL,
    IsSuspended     bit           NOT NULL CONSTRAINT DF_Cards_IsSuspended DEFAULT (0),
    IsLeech         bit           NOT NULL CONSTRAINT DF_Cards_IsLeech DEFAULT (0),   -- "zorlandıklarım"
    CreatedAt       datetime2(0)  NOT NULL CONSTRAINT DF_Cards_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time')),
    CONSTRAINT CK_Cards_Target CHECK (WordId IS NOT NULL OR SentenceId IS NOT NULL),
    CONSTRAINT CK_Cards_State  CHECK (State BETWEEN 0 AND 3)
);
GO
-- Aynı kullanıcı + kart tipi + içerik tek kart (NULL'lar eşit sayılır)
CREATE UNIQUE INDEX UX_Cards_User_Type_Target ON study.Cards(UserId, CardType, WordId, SentenceId);
-- EN KRİTİK SORGU: "bugün tekrar edilecek kartlar"
CREATE INDEX IX_Cards_Due ON study.Cards(UserId, DueAt)
    INCLUDE (CardType, WordId, SentenceId, State, Stability, Difficulty, Step, Reps, Lapses, LastReviewAt)
    WHERE IsSuspended = 0;
-- "Zorlandıklarım" listesi
CREATE INDEX IX_Cards_Leech ON study.Cards(UserId) INCLUDE (WordId, SentenceId, Lapses) WHERE IsLeech = 1;
-- Kelime bazlı ilerleme (Durumum paneli)
CREATE INDEX IX_Cards_User_Word ON study.Cards(UserId, WordId) INCLUDE (State, Stability, Lapses) WHERE WordId IS NOT NULL;
GO

CREATE TABLE study.ReviewLogs
(
    Id              bigint        IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReviewLogs PRIMARY KEY,
    CardId          bigint        NOT NULL CONSTRAINT FK_ReviewLogs_Cards REFERENCES study.Cards(Id),
    UserId          int           NOT NULL,     -- istatistik hızı için tekrar (denormalize)
    Rating          tinyint       NOT NULL,     -- 1 Again, 2 Hard, 3 Good, 4 Easy
    StateBefore     tinyint       NOT NULL,
    StabilityAfter  real          NOT NULL,
    DifficultyAfter real          NOT NULL,
    ElapsedDays     real          NOT NULL,     -- son tekrardan bu yana geçen gün
    ScheduledDays   real          NOT NULL,     -- bir sonraki tekrara kadar planlanan gün
    DurationMs      int           NULL,         -- cevaplama süresi
    ReviewedAt      datetime2(0)  NOT NULL,
    CONSTRAINT CK_ReviewLogs_Rating CHECK (Rating BETWEEN 1 AND 4)
);
GO
CREATE INDEX IX_ReviewLogs_User_Date ON study.ReviewLogs(UserId, ReviewedAt) INCLUDE (Rating, CardId, DurationMs);
CREATE INDEX IX_ReviewLogs_Card ON study.ReviewLogs(CardId);
GO

-- Seviye testinde veya elle "biliyorum" denilen kelimeler (kart oluşturulmaz)
CREATE TABLE study.UserKnownWords
(
    UserId      int           NOT NULL CONSTRAINT FK_UserKnownWords_Users REFERENCES auth.Users(Id),
    WordId      int           NOT NULL CONSTRAINT FK_UserKnownWords_Words REFERENCES content.Words(Id),
    Source      tinyint       NOT NULL,         -- 1 PlacementTest, 2 Manual
    MarkedAt    datetime2(0)  NOT NULL CONSTRAINT DF_UserKnownWords_MarkedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time')),
    CONSTRAINT PK_UserKnownWords PRIMARY KEY (UserId, WordId)
);
GO

-- Kullanıcının senaryo ilerlemesi
CREATE TABLE study.UserScenarioProgress
(
    UserId          int           NOT NULL CONSTRAINT FK_UserScenarioProgress_Users REFERENCES auth.Users(Id),
    ScenarioId      int           NOT NULL CONSTRAINT FK_UserScenarioProgress_Scenarios REFERENCES content.Scenarios(Id),
    StartedAt       datetime2(0)  NOT NULL,
    CompletedAt     datetime2(0)  NULL,
    LastStudiedAt   datetime2(0)  NULL,
    CONSTRAINT PK_UserScenarioProgress PRIMARY KEY (UserId, ScenarioId)
);
GO

-- Günlük özet: panel, ısı haritası, seri ve token limiti kontrolü TEK SATIRDAN okunur
CREATE TABLE study.DailyStats
(
    UserId          int       NOT NULL CONSTRAINT FK_DailyStats_Users REFERENCES auth.Users(Id),
    StatDate        date      NOT NULL,
    NewCount        smallint  NOT NULL CONSTRAINT DF_DailyStats_NewCount DEFAULT (0),
    ReviewCount     smallint  NOT NULL CONSTRAINT DF_DailyStats_ReviewCount DEFAULT (0),
    CorrectCount    smallint  NOT NULL CONSTRAINT DF_DailyStats_CorrectCount DEFAULT (0),
    StudySeconds    int       NOT NULL CONSTRAINT DF_DailyStats_StudySeconds DEFAULT (0),
    AiTokens        int       NOT NULL CONSTRAINT DF_DailyStats_AiTokens DEFAULT (0),
    AiMessages      smallint  NOT NULL CONSTRAINT DF_DailyStats_AiMessages DEFAULT (0),
    CONSTRAINT PK_DailyStats PRIMARY KEY (UserId, StatDate)
);
GO

/* =============================================================
   AI
   ============================================================= */
CREATE TABLE ai.Conversations
(
    Id              bigint         IDENTITY(1,1) NOT NULL CONSTRAINT PK_Conversations PRIMARY KEY,
    UserId          int            NOT NULL CONSTRAINT FK_Conversations_Users REFERENCES auth.Users(Id),
    ScenarioId      int            NULL CONSTRAINT FK_Conversations_Scenarios REFERENCES content.Scenarios(Id),
    Mode            tinyint        NOT NULL,     -- 1 RolePlay, 2 FreeTalk
    Title           nvarchar(150)  NULL,
    TotalTokens     int            NOT NULL CONSTRAINT DF_Conversations_TotalTokens DEFAULT (0),
    StartedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Conversations_StartedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time')),
    EndedAt         datetime2(0)   NULL
);
GO
CREATE INDEX IX_Conversations_User ON ai.Conversations(UserId, StartedAt DESC);
GO

CREATE TABLE ai.Messages
(
    Id              bigint         IDENTITY(1,1) NOT NULL CONSTRAINT PK_Messages PRIMARY KEY,
    ConversationId  bigint         NOT NULL CONSTRAINT FK_Messages_Conversations REFERENCES ai.Conversations(Id) ON DELETE CASCADE,
    Role            tinyint        NOT NULL,     -- 1 System, 2 User, 3 Assistant
    Content         nvarchar(max)  NOT NULL,
    FeedbackJson    nvarchar(max)  NULL,         -- kullanıcı mesajı için düzeltme/geri bildirim
    InputTokens     int            NULL,
    OutputTokens    int            NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Messages_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time'))
);
GO
CREATE INDEX IX_Messages_Conversation ON ai.Messages(ConversationId, Id);
GO

CREATE TABLE ai.WritingSubmissions
(
    Id              bigint         IDENTITY(1,1) NOT NULL CONSTRAINT PK_WritingSubmissions PRIMARY KEY,
    UserId          int            NOT NULL CONSTRAINT FK_WritingSubmissions_Users REFERENCES auth.Users(Id),
    ScenarioId      int            NULL CONSTRAINT FK_WritingSubmissions_Scenarios REFERENCES content.Scenarios(Id),
    PromptText      nvarchar(1000) NULL,
    UserText        nvarchar(max)  NOT NULL,
    CorrectedText   nvarchar(max)  NULL,
    FeedbackJson    nvarchar(max)  NULL,
    Score           tinyint        NULL,         -- 0..100
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_WritingSubmissions_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time')),
    CONSTRAINT CK_WritingSubmissions_Score CHECK (Score IS NULL OR Score <= 100)
);
GO
CREATE INDEX IX_WritingSubmissions_User ON ai.WritingSubmissions(UserId, CreatedAt DESC);
GO

-- Her LLM çağrısının detay kaydı (limit kontrolü DailyStats'tan yapılır, burası detay/rapor)
CREATE TABLE ai.Usage
(
    Id              bigint        IDENTITY(1,1) NOT NULL CONSTRAINT PK_Usage PRIMARY KEY,
    UserId          int           NULL,          -- NULL = sistem (admin içerik üretimi, cron)
    Provider        tinyint       NOT NULL,      -- 1 Gemini, 2 OpenRouter, 3 Groq
    Model           varchar(100)  NOT NULL,
    Feature         tinyint       NOT NULL,      -- 1 RolePlay, 2 Writing, 3 WordExplain, 4 ContentGeneration, 5 Other
    InputTokens     int           NOT NULL CONSTRAINT DF_Usage_InputTokens DEFAULT (0),
    OutputTokens    int           NOT NULL CONSTRAINT DF_Usage_OutputTokens DEFAULT (0),
    IsSuccess       bit           NOT NULL,
    IsFallback      bit           NOT NULL CONSTRAINT DF_Usage_IsFallback DEFAULT (0),
    DurationMs      int           NOT NULL,
    ErrorCode       varchar(50)   NULL,
    CorrelationId   varchar(50)   NULL,
    CreatedAt       datetime2(0)  NOT NULL CONSTRAINT DF_Usage_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time'))
);
GO
CREATE INDEX IX_Usage_User_Date ON ai.Usage(UserId, CreatedAt) INCLUDE (InputTokens, OutputTokens, Feature, IsSuccess);
CREATE INDEX IX_Usage_Date ON ai.Usage(CreatedAt);
GO

-- Ortak içerik cache'i: aynı istek tekrar LLM'e gitmez (kota koruma)
CREATE TABLE ai.ResponseCache
(
    Id              bigint         IDENTITY(1,1) NOT NULL CONSTRAINT PK_ResponseCache PRIMARY KEY,
    RequestHash     binary(32)     NOT NULL,     -- SHA-256(feature + model + prompt)
    Feature         tinyint        NOT NULL,
    ResponseJson    nvarchar(max)  NOT NULL,
    HitCount        int            NOT NULL CONSTRAINT DF_ResponseCache_HitCount DEFAULT (0),
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_ResponseCache_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time')),
    LastHitAt       datetime2(0)   NULL
);
GO
CREATE UNIQUE INDEX UX_ResponseCache_Hash ON ai.ResponseCache(RequestHash);
GO

/* =============================================================
   APP - sistem
   ============================================================= */
-- Dinamik ayarlar (Gemini anahtarı, SMTP vb.). Hassas olanlar AES-GCM ile şifreli.
CREATE TABLE app.Settings
(
    [Key]           varchar(100)   COLLATE Latin1_General_100_CI_AS NOT NULL CONSTRAINT PK_Settings PRIMARY KEY,
    Value           nvarchar(max)  NULL,
    IsEncrypted     bit            NOT NULL CONSTRAINT DF_Settings_IsEncrypted DEFAULT (0),
    GroupName       varchar(50)    NOT NULL,     -- 'Llm', 'Smtp', 'Jobs', 'General'
    DescriptionTr   nvarchar(300)  NULL,
    UpdatedAt       datetime2(0)   NULL,
    UpdatedBy       int            NULL
);
GO

-- Serilog SQL sink tablosu (Warning ve üstü)
CREATE TABLE app.Logs
(
    Id              bigint          IDENTITY(1,1) NOT NULL CONSTRAINT PK_Logs PRIMARY KEY,
    TimeStamp       datetime2(3)    NOT NULL,    -- Türkiye saati (uygulama tarafında çevrilir)
    Level           varchar(16)     NOT NULL,
    SourceApp       varchar(10)     NULL,        -- 'Api' / 'Web'
    Message         nvarchar(max)   NULL,
    MessageTemplate nvarchar(max)   NULL,
    Exception       nvarchar(max)   NULL,
    Properties      nvarchar(max)   NULL,        -- JSON
    CorrelationId   varchar(50)     NULL,
    UserId          int             NULL,
    RequestPath     nvarchar(400)   NULL,
    RequestMethod   varchar(10)     NULL,
    ClientIp        varchar(45)     NULL,
    MachineName     nvarchar(100)   NULL
);
GO
CREATE INDEX IX_Logs_TimeStamp ON app.Logs(TimeStamp DESC) INCLUDE (Level, SourceApp, UserId);
CREATE INDEX IX_Logs_Correlation ON app.Logs(CorrelationId) WHERE CorrelationId IS NOT NULL;
GO

CREATE TABLE app.EmailLogs
(
    Id              bigint          IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmailLogs PRIMARY KEY,
    UserId          int             NULL,
    MailType        tinyint         NOT NULL,    -- 1 DailyReminder, 2 WeeklyReport, 3 Test, 4 AccountInfo
    ToAddress       varchar(256)    NOT NULL,
    Subject         nvarchar(300)   NOT NULL,
    Status          tinyint         NOT NULL,    -- 1 Sent, 2 Failed
    ErrorMessage    nvarchar(1000)  NULL,
    DurationMs      int             NULL,
    CreatedAt       datetime2(0)    NOT NULL CONSTRAINT DF_EmailLogs_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time'))
);
GO
CREATE INDEX IX_EmailLogs_User ON app.EmailLogs(UserId, CreatedAt DESC) INCLUDE (MailType, Status);
GO

-- Admin işlemleri: kim, neyi, eski/yeni değer
CREATE TABLE app.AuditLogs
(
    Id              bigint          IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLogs PRIMARY KEY,
    ActorUserId     int             NULL,
    Action          varchar(50)     NOT NULL,    -- 'UserCreated', 'UserDeactivated', 'TokenLimitChanged' ...
    EntityType      varchar(50)     NOT NULL,
    EntityId        varchar(50)     NULL,
    OldValue        nvarchar(max)   NULL,        -- JSON (hassas alanlar maskeli)
    NewValue        nvarchar(max)   NULL,
    ClientIp        varchar(45)     NULL,
    CorrelationId   varchar(50)     NULL,
    CreatedAt       datetime2(0)    NOT NULL CONSTRAINT DF_AuditLogs_CreatedAt DEFAULT (CONVERT(datetime2(0), SYSDATETIMEOFFSET() AT TIME ZONE 'Turkey Standard Time'))
);
GO
CREATE INDEX IX_AuditLogs_Date ON app.AuditLogs(CreatedAt DESC) INCLUDE (ActorUserId, Action, EntityType);
CREATE INDEX IX_AuditLogs_Entity ON app.AuditLogs(EntityType, EntityId);
GO
