/*
    FixUpdatePlayedSongsNull.sql
    -------------------------------------------------------------------------
    Zweck:  Behebt die eigentliche Ursache der NULL-Werte in CDSong.CDSongCount.

    Ursache (per Audit-Trigger gefunden):
        Die Stored Procedure dbo.UpdatePlayedSongs (vom iTunes-Zaehler-Sync
        aufgerufen) rechnet:
            CDSongCount = T.PlayCount + CDSongWebCount
        CDSongWebCount ist nullable OHNE Default. Bei jedem Song, der nie ueber
        den Web-Player gespielt wurde, ist die Spalte NULL -> Zahl + NULL = NULL
        -> CDSongCount wird NULL. (Ein DEFAULT-Constraint hilft hier nicht, weil
        es ein UPDATE ist, kein INSERT.)

    Fix:    NULL-Arithmetik in der Prozedur absichern (ISNULL). Struktur sonst
            unveraendert. Zusaetzlich die aktuell betroffenen Zeilen bereinigen.

    Haertung: CDSongWebCount wie die anderen Zaehler-Spalten mit DEFAULT 0
    versehen und bestehende NULLs auffuellen - die Spalte war selbst ebenfalls
    nullable ohne Default und ist die Wurzel der NULL-Arithmetik.
    -------------------------------------------------------------------------
*/
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

ALTER PROCEDURE [dbo].[UpdatePlayedSongs]
AS
    UPDATE CDSong
       SET CDSongCount      = (ISNULL(T.PlayCount, 0) + ISNULL(CDSongWebCount, 0)),
           CDSongAppleCount = T.PlayCount
      FROM TempSongTable T
     INNER JOIN CDSong S ON T.SongId = S.CDSongId
RETURN 0
GO

-- Aktuell betroffene Zeilen bereinigen
UPDATE dbo.CDSong SET CDSongCount = 0 WHERE CDSongCount IS NULL;
GO

-- Haertung: CDSongWebCount dauerhaft absichern (war nullable ohne Default)
UPDATE dbo.CDSong SET CDSongWebCount = 0 WHERE CDSongWebCount IS NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_CDSong_CDSongWebCount')
    ALTER TABLE dbo.CDSong ADD CONSTRAINT DF_CDSong_CDSongWebCount DEFAULT (0) FOR CDSongWebCount;
GO
