/* =============================================================
   EnglishApp - Veritabanı oluşturma ve performans ayarları
   TEK SEFERLİK çalıştırılır (lokal kurulum).
   Çalıştırma: sqlcmd -S .\SQL2025 -U sa -P *** -C -I -i 000_CreateDatabase.sql
   ============================================================= */
USE master;
GO

IF DB_ID(N'EnglishApp') IS NULL
BEGIN
    -- Latin1_General_100: İngilizce "i/I" karşılaştırmaları doğru çalışsın diye
    -- (Turkish_CI_AS'de 'I' = 'ı' sayılır, İngilizce kelime aramalarını bozar)
    CREATE DATABASE EnglishApp COLLATE Latin1_General_100_CI_AS;
END
GO

ALTER DATABASE EnglishApp SET COMPATIBILITY_LEVEL = 170;          -- SQL Server 2025 optimizer
ALTER DATABASE EnglishApp SET RECOVERY SIMPLE;                    -- log şişmez, yazma hızlı
ALTER DATABASE EnglishApp SET AUTO_CLOSE OFF;                     -- her bağlantıda aç/kapa yok
ALTER DATABASE EnglishApp SET AUTO_SHRINK OFF;                    -- arka planda küçültme yok
ALTER DATABASE EnglishApp SET AUTO_CREATE_STATISTICS ON;
ALTER DATABASE EnglishApp SET AUTO_UPDATE_STATISTICS ON;
ALTER DATABASE EnglishApp SET AUTO_UPDATE_STATISTICS_ASYNC ON;    -- istatistik güncellemesi sorguyu bekletmez
ALTER DATABASE EnglishApp SET PAGE_VERIFY CHECKSUM;               -- veri bozulmasını yakalar
ALTER DATABASE EnglishApp SET TRUSTWORTHY OFF;                    -- güvenlik
ALTER DATABASE EnglishApp SET DB_CHAINING OFF;
ALTER DATABASE EnglishApp SET ANSI_NULLS ON;
ALTER DATABASE EnglishApp SET QUOTED_IDENTIFIER ON;
ALTER DATABASE EnglishApp SET ARITHABORT ON;
ALTER DATABASE EnglishApp SET ALLOW_SNAPSHOT_ISOLATION ON;
ALTER DATABASE EnglishApp SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE; -- okumalar yazmaları bloklamaz
ALTER DATABASE EnglishApp SET QUERY_STORE = ON;                   -- yavaş sorgu teşhisi için açık, yükü küçük
ALTER DATABASE EnglishApp SET QUERY_STORE (OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = AUTO, MAX_STORAGE_SIZE_MB = 100);
GO

USE EnglishApp;
GO
ALTER DATABASE SCOPED CONFIGURATION SET LEGACY_CARDINALITY_ESTIMATION = OFF;
ALTER DATABASE SCOPED CONFIGURATION SET PARAMETER_SNIFFING = ON;
ALTER DATABASE SCOPED CONFIGURATION SET OPTIMIZE_FOR_AD_HOC_WORKLOADS = ON; -- tek seferlik planlar cache'i şişirmez
GO
