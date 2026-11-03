/*
    FixCDSongCountDefault.sql
    -------------------------------------------------------------------------
    Zweck:  Behebt, dass CDSong.CDSongCount beim Anlegen neuer Songs gelegentlich
            NULL blieb statt 0.

    Ursache: CDSongCount war die einzige der vier Zaehler-Spalten OHNE DEFAULT-
             Constraint. Die Geschwister-Spalten CDSongCountOld, CDSongCountDiff
             und CDSongSecs haben jeweils DEFAULT ((0)). Der Hauptspeicherpfad
             (CDDatabase.SaveSong) setzt CDSongCount zwar explizit auf 0, aber
             jeder Insert-Pfad, der die Spalte auslaesst, hinterliess NULL.

    Fix:     1) bestehende NULL-Werte auf 0 setzen
             2) fehlenden DEFAULT (0) nachruesten (wie bei den Geschwister-Spalten)

    Rein additiv, idempotent. Betrifft die gemeinsame YAMSDatabase (NextYAMSKopie
    + YamsWebPlayer).
    -------------------------------------------------------------------------
*/
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    -- 1) Bestehende NULL-Werte initialisieren
    UPDATE dbo.CDSong
       SET CDSongCount = 0
     WHERE CDSongCount IS NULL;

    -- 2) Fehlenden DEFAULT-Constraint nachruesten (nur falls noch nicht vorhanden)
    IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
                    WHERE name = 'DF_CDSong_CDSongCount')
        ALTER TABLE dbo.CDSong
            ADD CONSTRAINT DF_CDSong_CDSongCount DEFAULT (0) FOR CDSongCount;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
