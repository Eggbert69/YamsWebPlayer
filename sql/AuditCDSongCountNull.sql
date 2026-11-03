/*
    AuditCDSongCountNull.sql
    -------------------------------------------------------------------------
    Zweck:  Herausfinden, WO gelegentlich eine NULL in CDSong.CDSongCount
            entsteht - egal ob per INSERT oder per UPDATE.

    Mechanik:
      - Audit-Tabelle dbo.CDSongCountNullAudit
      - Trigger dbo.trg_CDSong_NullCount_Audit (AFTER INSERT, UPDATE)
        Protokolliert NUR, wenn eine Zeile nach der Operation CDSongCount IS NULL
        hat UND die Operation diese NULL verursacht hat (Insert, oder Update das
        einen Wert -> NULL gesetzt hat). Im Normalbetrieb wird also NICHTS
        geschrieben -> praktisch kein Platzbedarf.
        Festgehalten werden: Zeitpunkt, Insert/Update, CDSongId/CDHeadId,
        Programm (APP_NAME), Host, Login und - best effort - das ausloesende
        SQL-Statement.

    Sicherheit:
      - Fail-safe: der gesamte Protokoll-Teil laeuft in TRY/CATCH. Ein Fehler
        beim Protokollieren darf das eigentliche Speichern NIEMALS blockieren.
      - Rein additiv. Der Default-Constraint aus FixCDSongCountDefault.sql deckt
        nur INSERTs ab; dieser Trigger deckt zusaetzlich UPDATEs ab.

    Auslesen:   SELECT * FROM dbo.CDSongCountNullAudit ORDER BY AuditId DESC;
    Entfernen:  DROP TRIGGER dbo.trg_CDSong_NullCount_Audit;
                DROP TABLE   dbo.CDSongCountNullAudit;
    -------------------------------------------------------------------------
*/
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID('dbo.CDSongCountNullAudit') IS NULL
BEGIN
    CREATE TABLE dbo.CDSongCountNullAudit (
        AuditId   INT IDENTITY(1,1) CONSTRAINT PK_CDSongCountNullAudit PRIMARY KEY,
        AtUtc     DATETIME2(0)  NOT NULL CONSTRAINT DF_CDSongCountNullAudit_AtUtc DEFAULT SYSUTCDATETIME(),
        Action    CHAR(1)       NOT NULL,                 -- 'I' = Insert, 'U' = Update
        CDSongId  INT           NULL,
        CDHeadId  INT           NULL,
        AppName   SYSNAME       NULL,                     -- APP_NAME()  -> welches Programm
        HostName  SYSNAME       NULL,                     -- HOST_NAME()
        LoginName SYSNAME       NULL,                     -- SUSER_SNAME()
        Stmt      NVARCHAR(MAX) NULL                      -- ausloesendes SQL (best effort)
    );
END
GO

IF OBJECT_ID('dbo.trg_CDSong_NullCount_Audit', 'TR') IS NOT NULL
    DROP TRIGGER dbo.trg_CDSong_NullCount_Audit;
GO

CREATE TRIGGER dbo.trg_CDSong_NullCount_Audit
ON dbo.CDSong
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    -- Schnell-Ausstieg: passiert nichts Verdaechtiges, sofort raus (kein Overhead)
    IF NOT EXISTS (SELECT 1 FROM inserted WHERE CDSongCount IS NULL)
        RETURN;

    BEGIN TRY
        DECLARE @stmt NVARCHAR(MAX) = NULL;

        -- Ausloesende Anweisung best effort (braucht VIEW SERVER STATE; sonst NULL)
        BEGIN TRY
            SELECT @stmt = event_info
            FROM sys.dm_exec_input_buffer(@@SPID, NULL);
        END TRY
        BEGIN CATCH
            SET @stmt = NULL;
        END CATCH

        INSERT INTO dbo.CDSongCountNullAudit
            (Action, CDSongId, CDHeadId, AppName, HostName, LoginName, Stmt)
        SELECT
            CASE WHEN d.CDSongId IS NULL THEN 'I' ELSE 'U' END,
            i.CDSongId, i.CDHeadId,
            APP_NAME(), HOST_NAME(), SUSER_SNAME(), @stmt
        FROM inserted i
        LEFT JOIN deleted d ON d.CDSongId = i.CDSongId
        WHERE i.CDSongCount IS NULL
          AND (d.CDSongId IS NULL          -- Insert mit NULL
               OR d.CDSongCount IS NOT NULL);  -- Update: war ein Wert, wurde NULL
    END TRY
    BEGIN CATCH
        -- Protokollierung darf das eigentliche Speichern NIEMALS blockieren.
        -- Fehler hier bewusst verschlucken.
        SET @stmt = @stmt;
    END CATCH
END
GO
