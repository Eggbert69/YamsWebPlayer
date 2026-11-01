-- ════════════════════════════════════════════════════════════════════════════
-- YamsWebPlayer – Vormerk-/Neubewertungstabelle CDSongReRate
-- ════════════════════════════════════════════════════════════════════════════
-- Zweck:
--   Nimmt vom Web-Player / der Handy-Fernbedienung vorgeschlagene Song-Noten
--   (1..5) auf, OHNE die produktive CDSong.CDSongNote zu verändern. Jeder offene
--   Eintrag merkt zugleich das Album (CDHeadId) zur Neubewertung vor.
--   Übernommen und das Album neu berechnet wird später bewusst in NextYAMSKopie;
--   dort wird ProcessedAt gesetzt (der Eintrag bleibt als Verlauf stehen).
--
-- Eigenschaften:
--   • Strikt additiv – verändert KEINE bestehende YAMS-Tabelle.
--   • Idempotent – mehrfaches Ausführen ist unschädlich.
--   • Wird beim Start des YamsWebPlayer automatisch sichergestellt (EnsureSchema
--     in Program.cs); dieses Skript dient der Transparenz / manuellem Setup.
--
-- Spalten:
--   ReRateId     fortlaufende ID (Primärschlüssel)
--   CDSongId     Verweis auf CDSong.CDSongId
--   CDHeadId     Album (denormalisiert → einfaches Gruppieren / Vormerken)
--   NoteNeu      vorgeschlagene Note 1..5
--   Source       Herkunft, 'W' = Web-Player (Platz für spätere Quellen)
--   CreatedAt    Zeitpunkt des Vorschlags (Serverzeit, Standard = jetzt)
--   ProcessedAt  NULL = offen · gesetzt = in NextYAMSKopie übernommen
--
-- Indizes:
--   UX_CDSongReRate_OpenSong  max. EIN offener Vorschlag je Song (ermöglicht Upsert)
--   IX_CDSongReRate_OpenHead  schnelles Finden der vorgemerkten Alben
-- ════════════════════════════════════════════════════════════════════════════

USE YAMSDatabase;
GO

IF OBJECT_ID('dbo.CDSongReRate','U') IS NULL
BEGIN
    CREATE TABLE dbo.CDSongReRate (
        ReRateId    INT IDENTITY(1,1) PRIMARY KEY,
        CDSongId    INT          NOT NULL,
        CDHeadId    INT          NOT NULL,
        NoteNeu     SMALLINT     NOT NULL,
        Source      CHAR(1)      NOT NULL CONSTRAINT DF_CDSongReRate_Source    DEFAULT 'W',
        CreatedAt   DATETIME2(0) NOT NULL CONSTRAINT DF_CDSongReRate_CreatedAt DEFAULT SYSDATETIME(),
        ProcessedAt DATETIME2(0) NULL
    );

    CREATE UNIQUE INDEX UX_CDSongReRate_OpenSong ON dbo.CDSongReRate (CDSongId)  WHERE ProcessedAt IS NULL;
    CREATE INDEX        IX_CDSongReRate_OpenHead ON dbo.CDSongReRate (CDHeadId) WHERE ProcessedAt IS NULL;

    PRINT 'CDSongReRate angelegt.';
END
ELSE
    PRINT 'CDSongReRate existiert bereits – nichts zu tun.';
GO
