-- ════════════════════════════════════════════════════════════════════════════
-- YamsWebPlayer – Verlaufstabelle CDSongHistory
-- ════════════════════════════════════════════════════════════════════════════
-- Zweck:
--   Protokolliert jeden über den Web-Player vollständig abgespielten Song.
--   Die eigentlichen Song-Metadaten (Titel, Dauer, Note, Zähler) werden NICHT
--   dupliziert, sondern bei der Anzeige über CDSongId aus CDSong/CDHead/CDArtist
--   gejoint – so bleibt der Verlauf immer konsistent mit der Bibliothek.
--
-- Eigenschaften:
--   • Strikt additiv – verändert KEINE bestehende YAMS-Tabelle.
--   • Idempotent – mehrfaches Ausführen ist unschädlich.
--   • Wird beim Start des YamsWebPlayer automatisch sichergestellt (EnsureSchema
--     in Program.cs); dieses Skript dient der Transparenz / manuellem Setup.
--
-- Spalten:
--   HistoryId   fortlaufende ID (Primärschlüssel)
--   CDSongId    Verweis auf CDSong.CDSongId
--   PlayedAt    Zeitpunkt der Wiedergabe (Serverzeit, Standard = jetzt)
--   Source      Herkunft, 'W' = Web-Player (Platz für spätere Quellen)
-- ════════════════════════════════════════════════════════════════════════════

USE YAMSDatabase;
GO

IF OBJECT_ID('dbo.CDSongHistory','U') IS NULL
BEGIN
    CREATE TABLE dbo.CDSongHistory (
        HistoryId INT IDENTITY(1,1) PRIMARY KEY,
        CDSongId  INT          NOT NULL,
        PlayedAt  DATETIME2(0) NOT NULL CONSTRAINT DF_CDSongHistory_PlayedAt DEFAULT SYSDATETIME(),
        Source    CHAR(1)      NOT NULL CONSTRAINT DF_CDSongHistory_Source   DEFAULT 'W'
    );

    CREATE INDEX IX_CDSongHistory_PlayedAt ON dbo.CDSongHistory (PlayedAt DESC);

    PRINT 'CDSongHistory angelegt.';
END
ELSE
    PRINT 'CDSongHistory existiert bereits – nichts zu tun.';
GO
