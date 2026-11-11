// ════════════════════════════════════════════════════════════════════════════════
// YamsWebPlayer – ASP.NET Core Minimal API  (.NET 8)
// ════════════════════════════════════════════════════════════════════════════════
//
// Zweck:
//   Web-basierter Musikplayer für die YAMS-Datenbank (SQL Server).
//   Liefert Album- und Song-Metadaten als JSON und streamt Audio-Dateien
//   direkt vom Server an den Browser (HTML5 <audio>-Element).
//   iTunes wird nicht mehr benötigt – die Wiedergabe erfolgt nativ im Browser.
//
// Architektur:
//   ┌─────────────────────────────────────────────────────────┐
//   │  Browser  (wwwroot/index.html + Vanilla-JavaScript)     │
//   │                                                         │
//   │  HTML5 <audio>  ◄──────────────────────────────────┐   │
//   │  Fetch API      ──► GET  /api/albums               │   │
//   │                 ──► GET  /api/albums/{id}           │   │
//   │                 ──► GET  /api/stream/{songId}  ─────┘   │
//   │                 ──► POST /api/songs/{id}/played         │
//   └─────────────────────────────────────────────────────────┘
//            │ HTTP (Port 5102)
//   ┌─────────────────────────────────────────────────────────┐
//   │  ASP.NET Core Minimal API  (diese Datei)                │
//   │  – JSON-Endpunkte für Alben, Songs, Künstler, Genres    │
//   │  – Audio-Streaming mit HTTP-Range-Support               │
//   │  – Spiel-Zähler-Update (CDSongCount / CDSongWebCount)   │
//   └─────────────────────────────────────────────────────────┘
//            │ ADO.NET (Microsoft.Data.SqlClient)
//   ┌─────────────────────────────────────────────────────────┐
//   │  SQL Server  EGGPC\SQLEXPRESS  –  YAMSDatabase          │
//   │  Tabellen: CDHead, CDSong, CDArtist, CDGenre, ...       │
//   └─────────────────────────────────────────────────────────┘
//
// Wichtige DB-Spaltentypen (Fallstricke):
//   CDSongNote     SmallInt (Int16) → immer Convert.ToInt32() verwenden
//   CDSongCount    SmallInt (Int16) → immer Convert.ToInt32() verwenden
//   CDSongTrackNum SmallInt (Int16) → immer Convert.ToInt32() verwenden
//
// Konfiguration:
//   appsettings.json:
//     ConnectionStrings:YamsDatabase  – SQL Server Connection String
//     Urls                            – http://0.0.0.0:5102
//
// ════════════════════════════════════════════════════════════════════════════════

using Microsoft.Data.SqlClient;

// ── App-Builder & Dienste ─────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

// Auf allen Netzwerkkarten lauschen (nicht nur localhost) → iPhone-Zugriff
builder.WebHost.UseUrls("http://0.0.0.0:5102");

// CORS: Alle Origins erlauben – notwendig damit der Browser Audio-Streams
// von der API abrufen darf, auch wenn Frontend und API auf unterschiedlichen
// Ports laufen (z. B. während der Entwicklung).
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();

// Globaler Middleware-Filter: OperationCanceledException beim Abbrechen einer
// Client-Anfrage (z. B. Browser-Tab geschlossen während Stream läuft)
// wird stillschweigend ignoriert, um Log-Spam zu vermeiden.
app.Use(async (ctx, next) =>
{
    try { await next(ctx); }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
});

// Statische Dateien aus wwwroot/ ausliefern (index.html, CSS, JS, Icons).
// UseDefaultFiles() sorgt dafür, dass "/" automatisch "index.html" liefert.
app.UseDefaultFiles();
app.UseStaticFiles();

// Connection String aus appsettings.json lesen.
// Fehlt er, wird die App sofort mit einer aussagekräftigen Fehlermeldung beendet.
var cs = builder.Configuration.GetConnectionString("YamsDatabase")
    ?? throw new InvalidOperationException("Connection string 'YamsDatabase' not found.");

// Factory-Funktion für neue DB-Verbindungen.
// Jede Anfrage öffnet ihre eigene Verbindung (kein Connection-Pooling-Problem,
// da SqlClient intern pooled).
SqlConnection NewConn() => new(cs);

// ── Schema sicherstellen (idempotent, strikt additiv) ──────────────────────────
// Legt die Verlaufstabelle CDSongHistory an, falls sie noch nicht existiert.
// Berührt KEINE bestehende YAMS-Tabelle. Schlägt das fehl (z. B. fehlende Rechte),
// läuft der Player trotzdem – der Verlauf ist dann einfach inaktiv.
try
{
    using var initConn = NewConn();
    initConn.Open();
    using var initCmd = initConn.CreateCommand();
    initCmd.CommandText = @"
IF OBJECT_ID('dbo.CDSongHistory','U') IS NULL
BEGIN
    CREATE TABLE dbo.CDSongHistory (
        HistoryId INT IDENTITY(1,1) PRIMARY KEY,
        CDSongId  INT          NOT NULL,
        PlayedAt  DATETIME2(0) NOT NULL CONSTRAINT DF_CDSongHistory_PlayedAt DEFAULT SYSDATETIME(),
        Source    CHAR(1)      NOT NULL CONSTRAINT DF_CDSongHistory_Source   DEFAULT 'W'
    );
    CREATE INDEX IX_CDSongHistory_PlayedAt ON dbo.CDSongHistory (PlayedAt DESC);
END";
    initCmd.ExecuteNonQuery();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[Warnung] Verlaufstabelle konnte nicht sichergestellt werden: {ex.Message}");
}

// Legt die Vormerk-/Neubewertungstabelle CDSongReRate an, falls noch nicht vorhanden.
// Nimmt vorgeschlagene Song-Noten auf, OHNE CDSongNote zu verändern. Ebenfalls strikt
// additiv; schlägt es fehl, läuft der Player weiter – die Neubewertung ist dann inaktiv.
try
{
    using var initConn = NewConn();
    initConn.Open();
    using var initCmd = initConn.CreateCommand();
    initCmd.CommandText = @"
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
END";
    initCmd.ExecuteNonQuery();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[Warnung] Neubewertungstabelle konnte nicht sichergestellt werden: {ex.Message}");
}

// ════════════════════════════════════════════════════════════════════════════════
// HILFSFUNKTIONEN
// ════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Bindet die Suchwerte als parametrisierte SQL-Parameter an das Command.
/// Wird zusammen mit <see cref="BuildWhere"/> verwendet, damit WHERE-Klausel
/// und Parameter immer synchron sind.
///
/// Genres und XGenres werden als dynamische @Genre0, @Genre1, ... Parameter
/// übergeben, weil SQL Server keine Array-Parameter kennt.
/// </summary>
/// <param name="cmd">Das SqlCommand, dem die Parameter hinzugefügt werden.</param>
/// <param name="artist">Teilstring des Künstlernamens (LIKE-Suche).</param>
/// <param name="yearFrom">Frühestes Erscheinungsjahr (inklusiv).</param>
/// <param name="yearTo">Spätestes Erscheinungsjahr (inklusiv).</param>
/// <param name="genres">Komma-getrennte Genre-Liste (LIKE-Suche, OR-verknüpft).</param>
/// <param name="maxRating">Maximale Wertung (1.0 = beste, 5.0 = schlechteste).</param>
/// <param name="typ">Typ-Filter: S=Studio, L=Live, M=Mixed (komma-getrennt).</param>
/// <param name="cat">Kategorie-Filter: O=Original, S=Single, C=Compilation, M=Mix, B=Bootleg.</param>
/// <param name="xgenres">Auszuschließende Genres (NOT LIKE, AND-verknüpft).</param>
static void AddSearchParams(SqlCommand cmd, string? artist, int? yearFrom, int? yearTo,
    string? genres, double? maxRating, string? typ, string? cat, string? xgenres = null)
{
    if (!string.IsNullOrWhiteSpace(artist))    cmd.Parameters.AddWithValue("@Artist",    artist.Trim());
    if (yearFrom is > 0)                       cmd.Parameters.AddWithValue("@YearFrom",  yearFrom.Value);
    if (yearTo   is > 0)                       cmd.Parameters.AddWithValue("@YearTo",    yearTo.Value);
    if (!string.IsNullOrWhiteSpace(genres))
    {
        var gl = genres.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < gl.Length; i++)
            cmd.Parameters.AddWithValue($"@Genre{i}", gl[i]);
    }
    if (!string.IsNullOrWhiteSpace(xgenres))
    {
        var xl = xgenres.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < xl.Length; i++)
            cmd.Parameters.AddWithValue($"@XGenre{i}", xl[i]);
    }
    if (maxRating is > 0 and < 5.0)            cmd.Parameters.AddWithValue("@MaxRating", maxRating.Value);
}

/// <summary>
/// Erzeugt die Liste der WHERE-Bedingungen für Album-Suchabfragen.
/// Die zurückgegebenen Strings werden mit " AND " zusammengefügt.
///
/// Hinweis: typ und cat werden direkt in den String eingebaut (whitelisted),
/// da sie nur einzelne Buchstaben aus einer festen Menge enthalten dürfen
/// (SQL-Injection ist damit ausgeschlossen).
/// </summary>
/// <returns>Liste von SQL-Bedingungsstrings, leer wenn keine Filter aktiv.</returns>
static List<string> BuildWhere(string? artist, int? yearFrom, int? yearTo,
    string? genres, double? maxRating, string? typ, string? cat, string? xgenres = null)
{
    var w = new List<string>();

    // Künstler: Suche in Name UND Sortierfeld (z. B. "Beatles, The" vs "The Beatles")
    if (!string.IsNullOrWhiteSpace(artist))
        w.Add("(a.CDArtistName LIKE '%' + @Artist + '%' OR a.CDArtistSort LIKE '%' + @Artist + '%')");

    if (yearFrom is > 0)   w.Add("h.CDHeadJahr >= @YearFrom");
    if (yearTo   is > 0)   w.Add("h.CDHeadJahr <= @YearTo");

    // Genres: Bei einem Genre einfaches LIKE, bei mehreren OR-verknüpft
    if (!string.IsNullOrWhiteSpace(genres))
    {
        var gl = genres.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (gl.Length == 1)
            w.Add("RTRIM(h.CDHeadGenre) LIKE '%' + @Genre0 + '%'");
        else
        {
            var likes = string.Join(" OR ", gl.Select((_, i) => $"RTRIM(h.CDHeadGenre) LIKE '%' + @Genre{i} + '%'"));
            w.Add($"({likes})");
        }
    }

    // Ausgeschlossene Genres: jedes einzeln als NOT LIKE (AND-verknüpft)
    if (!string.IsNullOrWhiteSpace(xgenres))
    {
        var xl = xgenres.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var (_, i) in xl.Select((v, i) => (v, i)))
            w.Add($"RTRIM(h.CDHeadGenre) NOT LIKE '%' + @XGenre{i} + '%'");
    }

    // Wertung: Nur wenn ein sinnvoller Wert übergeben wurde (0 und 5.0 = kein Filter)
    if (maxRating is > 0 and < 5.0)
        w.Add("h.CDHeadWertung > 0 AND h.CDHeadWertung <= @MaxRating");

    // Typ-Filter: Whitelist-Prüfung verhindert SQL-Injection
    // Erlaubte Werte: S (Studio), L (Live), M (Mixed)
    if (!string.IsNullOrWhiteSpace(typ))
    {
        var vals = typ.Split(',').Select(v => v.Trim().ToUpper())
                      .Where(v => v.Length == 1 && "SLM".Contains(v))
                      .Select(v => $"'{v}'").ToList();
        if (vals.Count > 0) w.Add($"RTRIM(h.CDHeadTyp) IN ({string.Join(",", vals)})");
    }

    // Kategorie-Filter: Whitelist-Prüfung verhindert SQL-Injection
    // Erlaubte Werte: O (Original), S (Single), C (Compilation), M (Mix), B (Bootleg)
    if (!string.IsNullOrWhiteSpace(cat))
    {
        var vals = cat.Split(',').Select(v => v.Trim().ToUpper())
                      .Where(v => v.Length == 1 && "OSCMB".Contains(v))
                      .Select(v => $"'{v}'").ToList();
        if (vals.Count > 0) w.Add($"RTRIM(h.CDHeadCat) IN ({string.Join(",", vals)})");
    }

    return w;
}

/// <summary>
/// Ermittelt den MIME-Type für Bild-Dateien anhand der Dateiendung.
/// Wird für Cover-Art-Bilder verwendet.
/// Unbekannte Endungen werden als JPEG behandelt (häufigstes Format).
/// </summary>
static string GetImageMime(string path) =>
    Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png"  => "image/png",
        ".gif"  => "image/gif",
        ".bmp"  => "image/bmp",
        ".webp" => "image/webp",
        _       => "image/jpeg"
    };

/// <summary>
/// Ermittelt den MIME-Type für Audio-Dateien anhand der Dateiendung.
/// Wird beim Streaming-Endpunkt als Content-Type Header gesetzt.
/// Korrekte MIME-Types sind wichtig damit iOS/Safari den Stream akzeptiert.
/// Unbekannte Endungen → audio/mpeg (MP3 als häufigstes Format).
/// </summary>
static string GetAudioMime(string path) =>
    Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".flac" => "audio/flac",
        ".m4a"  => "audio/mp4",
        ".aac"  => "audio/aac",
        ".ogg"  => "audio/ogg",
        ".wav"  => "audio/wav",
        _       => "audio/mpeg"   // .mp3 und alle unbekannten Formate
    };

// ════════════════════════════════════════════════════════════════════════════════
// API-ENDPUNKTE
// ════════════════════════════════════════════════════════════════════════════════

// ── GET /api/artists?q={suchbegriff} ─────────────────────────────────────────
// Autovervollständigung für die Künstler-Suche im Frontend.
// Gibt maximal 25 Treffer zurück, sortiert nach CDArtistSort.
// Sucht in CDArtistSort (z. B. "Beatles, The") UND CDArtistName ("The Beatles"),
// damit der Benutzer beides eingeben kann.
// Leerer Suchbegriff → leeres Array (kein unnötiger DB-Zugriff).
app.MapGet("/api/artists", async (string? q) =>
{
    if (string.IsNullOrWhiteSpace(q)) return Results.Json(Array.Empty<string>());
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT TOP 25 RTRIM(CDArtistName)
        FROM   CDArtist
        WHERE  CDArtistSort LIKE @Q + '%' OR CDArtistName LIKE @Q + '%'
        ORDER  BY CDArtistSort";
    cmd.Parameters.AddWithValue("@Q", q.Trim());
    var list = new List<string>();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync()) list.Add(r.GetString(0).Trim());
    return Results.Json(list);
});

// ── GET /api/genres/groups ────────────────────────────────────────────────────
// Liefert alle Genres gruppiert nach CDGenreGroup für das Genre-Dropdown
// im Frontend. Struktur: [ { group: "Rock", genres: ["Rock", "Punk", ...] }, ... ]
// Sortiert alphabetisch nach Gruppe und Genrename.
app.MapGet("/api/genres/groups", async () =>
{
    // SortedDictionary garantiert alphabetische Reihenfolge der Gruppen
    var groups = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT RTRIM(CDGenreGroup), RTRIM(CDGenreName)
        FROM   CDGenre
        WHERE  CDGenreGroup IS NOT NULL AND CDGenreName IS NOT NULL
        ORDER  BY RTRIM(CDGenreGroup), RTRIM(CDGenreName)";
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        var grp  = r.GetString(0).Trim();
        var name = r.GetString(1).Trim();
        if (!groups.ContainsKey(grp)) groups[grp] = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        groups[grp].Add(name);
    }
    return Results.Json(groups.Select(kv => new { group = kv.Key, genres = kv.Value.ToList() }));
});

// ── GET /api/genres ───────────────────────────────────────────────────────────
// Liefert alle in CDHead verwendeten Genre-Strings als flache Liste.
// (Distinct, alphabetisch, ohne Leerzeilen)
// Wird als Fallback genutzt wenn /api/genres/groups nichts liefert.
app.MapGet("/api/genres", async () =>
{
    var list = new List<string>();
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT DISTINCT RTRIM(CDHeadGenre)
        FROM   CDHead
        WHERE  CDHeadGenre IS NOT NULL AND LTRIM(RTRIM(CDHeadGenre)) <> ''
        ORDER  BY 1";
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync()) list.Add(r.GetString(0).Trim());
    return Results.Json(list);
});

// ── GET /api/albums/playstatus ────────────────────────────────────────────────
// Berechnet den Wiedergabe-Status für jedes Album, aufgeschlüsselt nach Wertung.
// Wird vom Frontend genutzt, um Alben farblich zu markieren
// (z. B. "alle Top-Songs gespielt" vs. "noch ungehörte 5-Sterne-Songs").
//
// Rückgabe pro Album:
//   n1Count/n2Count/n3Count/n4Count  – Anzahl Songs mit Note 1-4 und Count=0 (ungespielt)
//   n1Played/n2Played/n3Played/n4Played – Anzahl Songs mit Note 1-4 und Count>0 (gespielt)
//
// Hinweis: Note 4 und 5 werden zusammengefasst (beide = "Top-Bewertung").
//          Nur Originalsongs (CDSongOrg='T') werden gezählt.
app.MapGet("/api/albums/playstatus", async () =>
{
    const string sql = @"
        SELECT CDHeadId,
               SUM(CASE WHEN CDSongNote = 1 AND CDSongCount = 0 THEN 1 ELSE 0 END) AS N1Count,
               SUM(CASE WHEN CDSongNote = 2 AND CDSongCount = 0 THEN 1 ELSE 0 END) AS N2Count,
               SUM(CASE WHEN CDSongNote = 3 AND CDSongCount = 0 THEN 1 ELSE 0 END) AS N3Count,
               SUM(CASE WHEN (CDSongNote = 4 OR CDSongNote = 5) AND CDSongCount = 0 THEN 1 ELSE 0 END) AS N4Count,
               SUM(CASE WHEN CDSongNote = 1 AND CDSongCount > 0 THEN 1 ELSE 0 END) AS N1Played,
               SUM(CASE WHEN CDSongNote = 2 AND CDSongCount > 0 THEN 1 ELSE 0 END) AS N2Played,
               SUM(CASE WHEN CDSongNote = 3 AND CDSongCount > 0 THEN 1 ELSE 0 END) AS N3Played,
               SUM(CASE WHEN (CDSongNote = 4 OR CDSongNote = 5) AND CDSongCount > 0 THEN 1 ELSE 0 END) AS N4Played
        FROM   CDSong
        WHERE  CDSongOrg = 'T'
        GROUP  BY CDHeadId";
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = new SqlCommand(sql, conn);
    await using var r   = await cmd.ExecuteReaderAsync();
    var result = new List<object>();
    while (await r.ReadAsync())
        result.Add(new {
            albumId  = r.GetInt32(0),
            n1Count  = r.GetInt32(1), n2Count  = r.GetInt32(2),
            n3Count  = r.GetInt32(3), n4Count  = r.GetInt32(4),
            n1Played = r.GetInt32(5), n2Played = r.GetInt32(6),
            n3Played = r.GetInt32(7), n4Played = r.GetInt32(8)
        });
    return Results.Json(result);
});

// ── GET /api/years ─────────────────────────────────────────────────────────────
// Liefert das früheste und späteste Erscheinungsjahr in der Datenbank.
// Wird vom Frontend genutzt, um die Jahrgangs-Slider korrekt zu initialisieren.
// Jahre vor 1900 werden ignoriert (Datenfehler).
app.MapGet("/api/years", async () =>
{
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT MIN(CDHeadJahr), MAX(CDHeadJahr) FROM CDHead WHERE CDHeadJahr > 1900";
    await using var r = await cmd.ExecuteReaderAsync();
    if (await r.ReadAsync() && !r.IsDBNull(0))
        return Results.Json(new { min = Convert.ToInt32(r[0]), max = Convert.ToInt32(r[1]) });
    // Fallback falls keine Daten vorhanden
    return Results.Json(new { min = 1950, max = DateTime.Now.Year });
});

// ── GET /api/albums ────────────────────────────────────────────────────────────
// Hauptendpunkt für die Album-Liste. Unterstützt:
//   - Volltext-Filter nach Künstler, Genre, Jahr, Typ, Kategorie, Wertung
//   - Auszuschließende Genres (xgenres)
//   - Sortierung nach artist/year/rating/title/played (je asc/desc)
//   - Server-seitiges Paging (page + pageSize)
//
// Rückgabe: { total, page, pageSize, albums: [...] }
//   total = Gesamtanzahl der Treffer (für Pager-Anzeige im Frontend)
//
// Zwei DB-Queries: erst COUNT für das Total, dann die eigentliche Seite.
// OFFSET/FETCH wird von SQL Server 2012+ unterstützt.
app.MapGet("/api/albums", async (
    string? artist, int? yearFrom, int? yearTo, string? genres, string? xgenres,
    double? maxRating, string? typ, string? cat,
    int page = 1, int pageSize = 48, string? sortBy = "artist", string? sortDir = "asc") =>
{
    var whereParts  = BuildWhere(artist, yearFrom, yearTo, genres, maxRating, typ, cat, xgenres);
    var whereClause = whereParts.Count > 0 ? "WHERE " + string.Join(" AND ", whereParts) : "";

    bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);

    // Sortierlogik: immer Sekundär-Sortierung um stabile Reihenfolge beim Paging
    // zu gewährleisten (gleiche Wertung → nach Künstler sortiert)
    var orderClause = sortBy switch
    {
        "year"   => desc ? "h.CDHeadJahr ASC, a.CDArtistSort, h.CDHeadTitel"
                         : "h.CDHeadJahr DESC, a.CDArtistSort, h.CDHeadTitel",
        "rating" => desc ? "ISNULL(h.CDHeadWertung,9999) DESC, a.CDArtistSort, h.CDHeadTitel"
                         : "ISNULL(h.CDHeadWertung,9999) ASC, a.CDArtistSort, h.CDHeadTitel",
        "title"  => desc ? "RTRIM(h.CDHeadTitel) DESC, a.CDArtistSort"
                         : "RTRIM(h.CDHeadTitel) ASC, a.CDArtistSort",
        "played" => desc ? "h.CDHeadPlayed ASC, a.CDArtistSort"
                         : "h.CDHeadPlayed DESC, a.CDArtistSort",
        _        => desc ? "a.CDArtistSort DESC, h.CDHeadJahr, RTRIM(h.CDHeadTitel)"
                         : "a.CDArtistSort ASC, h.CDHeadJahr, RTRIM(h.CDHeadTitel)"
    };

    // Seite 1 = Offset 0, Seite 2 = Offset pageSize, usw.
    int offset = (Math.Max(1, page) - 1) * pageSize;

    await using var conn = NewConn();
    await conn.OpenAsync();

    // Schritt 1: Gesamtanzahl für Pager
    int total;
    await using (var countCmd = conn.CreateCommand())
    {
        countCmd.CommandText = $@"
            SELECT COUNT(*)
            FROM   CDHead h
            JOIN   CDArtist a ON h.CDArtistId = a.CDArtistId
            {whereClause}";
        AddSearchParams(countCmd, artist, yearFrom, yearTo, genres, maxRating, typ, cat, xgenres);
        total = (int)(await countCmd.ExecuteScalarAsync())!;
    }

    // Schritt 2: Aktuelle Seite laden
    var albums = new List<object>();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $@"
        SELECT h.CDHeadId,
               RTRIM(a.CDArtistName)    AS ArtistName,
               RTRIM(h.CDHeadTitel)     AS Titel,
               h.CDHeadJahr,
               RTRIM(h.CDHeadGenre)     AS Genre,
               h.CDHeadWertung,
               h.CDHeadTrackCount,
               h.CDHeadTotalSec,
               RTRIM(h.CDHeadCoverPfad) AS CoverPfad,
               RTRIM(h.CDHeadTyp)       AS Typ,
               RTRIM(h.CDLabelName)     AS Label,
               h.CDHeadPlayed,
               h.CDHeadAMG
        FROM   CDHead h
        JOIN   CDArtist a ON h.CDArtistId = a.CDArtistId
        {whereClause}
        ORDER  BY {orderClause}
        OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY";
    AddSearchParams(cmd, artist, yearFrom, yearTo, genres, maxRating, typ, cat, xgenres);

    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        albums.Add(new
        {
            id         = Convert.ToInt32(r[0]),
            artist     = r.GetString(1),
            title      = r.GetString(2),
            year       = r.IsDBNull(3)  ? 0   : Convert.ToInt32(r[3]),
            genre      = r.IsDBNull(4)  ? ""  : r.GetString(4).Trim(),
            rating     = r.IsDBNull(5)  ? 0.0 : Convert.ToDouble(r[5]),
            trackCount = r.IsDBNull(6)  ? 0   : Convert.ToInt32(r[6]),
            totalSec   = r.IsDBNull(7)  ? 0   : Convert.ToInt32(r[7]),
            coverPath  = r.IsDBNull(8)  ? null: r.GetString(8).Trim(),
            typ        = r.IsDBNull(9)  ? ""  : r.GetString(9).Trim(),
            label      = r.IsDBNull(10) ? ""  : r.GetString(10).Trim(),
            played     = r.IsDBNull(11) ? 0   : Convert.ToInt32(r[11]),
            amg        = r.IsDBNull(12) ? 0.0 : Convert.ToDouble(r[12]),
        });
    }

    return Results.Json(new { total, page, pageSize, albums });
});

// ── GET /api/albums/{id} ───────────────────────────────────────────────────────
// Liefert ein einzelnes Album mit allen seinen Songs (Tracks).
// Wird aufgerufen wenn der Benutzer ein Album aufklappt oder abspielt.
//
// Rückgabe: { album: {...}, tracks: [{...}, ...] }
//
// Tracks werden nach Tracknummer sortiert (dann nach Titel als Fallback).
// CDSongOrg = 'T' bedeutet Original-Track (im Gegensatz zu Bonus, Live-Version etc.)
//
// ACHTUNG: CDSongNote, CDSongCount und CDSongTrackNum sind SmallInt (Int16)!
// Deshalb Convert.ToInt32() statt GetInt32() verwenden – sonst InvalidCastException.
app.MapGet("/api/albums/{id:int}", async (int id) =>
{
    await using var conn = NewConn();
    await conn.OpenAsync();

    // Album-Stammdaten laden (inkl. CDHeadPfad für die Datei-Suche beim Streaming)
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT h.CDHeadId,
               RTRIM(a.CDArtistName),
               RTRIM(h.CDHeadTitel),
               h.CDHeadJahr,
               RTRIM(h.CDHeadGenre),
               h.CDHeadWertung,
               h.CDHeadTrackCount,
               h.CDHeadTotalSec,
               RTRIM(h.CDHeadCoverPfad),
               RTRIM(h.CDHeadTyp),
               RTRIM(h.CDLabelName),
               h.CDHeadPlayed,
               h.CDHeadAMG,
               RTRIM(h.CDHeadPfad)
        FROM   CDHead h
        JOIN   CDArtist a ON h.CDArtistId = a.CDArtistId
        WHERE  h.CDHeadId = @Id";
    cmd.Parameters.AddWithValue("@Id", id);

    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) return Results.NotFound();

    var album = new
    {
        id         = Convert.ToInt32(r[0]),
        artist     = r.GetString(1),
        title      = r.GetString(2),
        year       = r.IsDBNull(3)  ? 0   : Convert.ToInt32(r[3]),
        genre      = r.IsDBNull(4)  ? ""  : r.GetString(4).Trim(),
        rating     = r.IsDBNull(5)  ? 0.0 : Convert.ToDouble(r[5]),
        trackCount = r.IsDBNull(6)  ? 0   : Convert.ToInt32(r[6]),
        totalSec   = r.IsDBNull(7)  ? 0   : Convert.ToInt32(r[7]),
        coverPath  = r.IsDBNull(8)  ? null: r.GetString(8).Trim(),
        typ        = r.IsDBNull(9)  ? ""  : r.GetString(9).Trim(),
        label      = r.IsDBNull(10) ? ""  : r.GetString(10).Trim(),
        played     = r.IsDBNull(11) ? 0   : Convert.ToInt32(r[11]),
        amg        = r.IsDBNull(12) ? 0.0 : Convert.ToDouble(r[12]),
        path       = r.IsDBNull(13) ? ""  : r.GetString(13).Trim(),
    };
    await r.CloseAsync();

    // Song-Liste des Albums laden.
    // SongArtist: Manche Songs haben einen eigenen Künstler (z. B. Features),
    // der dann statt des Album-Künstlers angezeigt wird.
    var tracks = new List<object>();
    await using var tc = conn.CreateCommand();
    tc.CommandText = @"
        SELECT s.CDSongId,
               s.CDSongTrackNum,
               RTRIM(s.CDSongTitel),
               ISNULL(DATEDIFF(SECOND, '00:00:00', s.CDSongDauer), 0) AS CDSongSecs,
               s.CDSongNote,
               RTRIM(ISNULL(sa.CDArtistName, a.CDArtistName)) AS SongArtist,
               RTRIM(ISNULL(s.CDSongOrg, 'T')) AS CDSongOrg,
               ISNULL(s.CDSongCount, 0) AS CDSongCount
        FROM   CDSong s
        JOIN   CDHead  h  ON s.CDHeadId   = h.CDHeadId
        JOIN   CDArtist a ON h.CDArtistId = a.CDArtistId
        LEFT JOIN CDArtist sa ON s.SongArtistId = sa.CDArtistId
        WHERE  s.CDHeadId = @Id
        ORDER  BY s.CDSongTrackNum, s.CDSongTitel";
    tc.Parameters.AddWithValue("@Id", id);

    await using var tr = await tc.ExecuteReaderAsync();
    while (await tr.ReadAsync())
    {
        tracks.Add(new
        {
            id         = Convert.ToInt32(tr[0]),
            trackNum   = tr.IsDBNull(1) ? 0  : Convert.ToInt32(tr[1]),  // SmallInt!
            title      = tr.GetString(2),
            secs       = tr.IsDBNull(3) ? 0  : Convert.ToInt32(tr[3]),
            rating     = tr.IsDBNull(4) ? 0  : Convert.ToInt32(tr[4]),  // SmallInt!
            songArtist = tr.IsDBNull(5) ? "" : tr.GetString(5).Trim(),
            org        = tr.IsDBNull(6) ? "T" : tr.GetString(6).Trim(),
            count      = tr.IsDBNull(7) ? 0   : Convert.ToInt32(tr[7]), // SmallInt!
        });
    }

    return Results.Json(new { album, tracks });
});

// ── POST /api/songs/{songId}/played ───────────────────────────────────────────
// Wird vom Browser aufgerufen wenn ein Song vollständig abgespielt wurde
// (audio.ended Event im Frontend).
//
// Erhöht gleichzeitig:
//   CDSongCount    – Gesamtzähler (iTunes + Web Player)
//   CDSongWebCount – Zähler nur für Web-Player-Wiedergaben
//
// ISNULL(..., 0) + 1: Verhindert dass NULL-Werte zu NULL+1=NULL werden.
// HTTP 200 OK  → Song gefunden und aktualisiert
// HTTP 404     → Song-ID nicht in der Datenbank
app.MapPost("/api/songs/{songId:int}/played", async (int songId) =>
{
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = @"
            UPDATE CDSong
            SET    CDSongCount    = ISNULL(CDSongCount,    0) + 1,
                   CDSongWebCount = ISNULL(CDSongWebCount, 0) + 1
            WHERE  CDSongId = @Id";
        cmd.Parameters.AddWithValue("@Id", songId);
        var rows = await cmd.ExecuteNonQueryAsync();
        if (rows == 0) return Results.NotFound();
    }

    // Verlauf mitschreiben – best effort. Ein Fehler hier (z. B. Tabelle fehlt)
    // darf die bereits erfolgte Zähler-Aktualisierung nicht beeinträchtigen.
    try
    {
        await using var hist = conn.CreateCommand();
        hist.CommandText = "INSERT INTO CDSongHistory (CDSongId) VALUES (@Id)";
        hist.Parameters.AddWithValue("@Id", songId);
        await hist.ExecuteNonQueryAsync();
    }
    catch { /* Verlauf ist optional */ }

    return Results.Ok();
});

// ── GET /api/history?limit=200&offset=0 ────────────────────────────────────────
// Liefert die zuletzt gespielten Songs (neueste zuerst), inkl. Metadaten aus
// CDSong/CDHead/CDArtist. Paging über limit/offset für "Mehr laden".
//
// playedAt wird als sortierbarer ISO-String ohne Zeitzone geliefert
// (SYSDATETIME = Serverzeit) und im Browser als lokale Zeit interpretiert.
// Hinweis: CDSongNote ist SmallInt → Convert.ToInt32.
app.MapGet("/api/history", async (int limit = 200, int offset = 0) =>
{
    if (limit < 1)    limit = 1;
    if (limit > 1000) limit = 1000;
    if (offset < 0)   offset = 0;

    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT hi.HistoryId,
               hi.PlayedAt,
               s.CDSongId,
               RTRIM(s.CDSongTitel)                            AS Titel,
               RTRIM(ISNULL(sa.CDArtistName, a.CDArtistName))  AS Artist,
               h.CDHeadId,
               RTRIM(h.CDHeadTitel)                            AS AlbumTitel,
               h.CDHeadJahr,
               ISNULL(DATEDIFF(SECOND, '00:00:00', s.CDSongDauer), 0) AS Secs,
               ISNULL(s.CDSongNote,  0)                        AS Note,
               ISNULL(s.CDSongCount, 0)                        AS Cnt
        FROM   CDSongHistory hi
        JOIN   CDSong   s  ON hi.CDSongId  = s.CDSongId
        JOIN   CDHead   h  ON s.CDHeadId   = h.CDHeadId
        JOIN   CDArtist a  ON h.CDArtistId = a.CDArtistId
        LEFT JOIN CDArtist sa ON s.SongArtistId = sa.CDArtistId
        ORDER  BY hi.PlayedAt DESC, hi.HistoryId DESC
        OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY";
    cmd.Parameters.AddWithValue("@Offset", offset);
    cmd.Parameters.AddWithValue("@Limit",  limit);

    var items = new List<object>();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        items.Add(new
        {
            historyId  = Convert.ToInt32(r[0]),
            playedAt   = r.GetDateTime(1).ToString("s"),   // yyyy-MM-ddTHH:mm:ss
            songId     = Convert.ToInt32(r[2]),
            title      = r.IsDBNull(3)  ? ""  : r.GetString(3),
            artist     = r.IsDBNull(4)  ? ""  : r.GetString(4).Trim(),
            albumId    = Convert.ToInt32(r[5]),
            albumTitle = r.IsDBNull(6)  ? ""  : r.GetString(6).Trim(),
            year       = r.IsDBNull(7)  ? 0   : Convert.ToInt32(r[7]),
            secs       = r.IsDBNull(8)  ? 0   : Convert.ToInt32(r[8]),
            note       = r.IsDBNull(9)  ? 0   : Convert.ToInt32(r[9]),
            count      = r.IsDBNull(10) ? 0   : Convert.ToInt32(r[10]),
        });
    }
    return Results.Json(items);
});

// ── DELETE /api/history ────────────────────────────────────────────────────────
// Leert den gesamten Abspielverlauf. Die Spielzähler (CDSongCount) bleiben unberührt.
app.MapDelete("/api/history", async () =>
{
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM CDSongHistory";
    var n = await cmd.ExecuteNonQueryAsync();
    return Results.Json(new { deleted = n });
});

// ── GET /api/stream/{songId} ───────────────────────────────────────────────────
// Kernfunktion des Players: Sucht die Audio-Datei auf der Festplatte und
// streamt sie mit HTTP Range-Support an den Browser.
//
// HTTP Range-Support (enableRangeProcessing: true) ist notwendig für:
//   - Seeking im Audio-Player (Klick auf Fortschrittsbalken)
//   - iOS/Safari: ohne Range-Support startet die Wiedergabe nicht
//   - Autoplay nach Verbindungsunterbrechung
//
// Datei-Suche (zweistufig):
//   Stufe 1 – Tracknummer-Präfix:
//     Versucht die Datei anhand der Tracknummer zu finden.
//     Unterstützte Formate: "03 Titel.mp3", "03-Titel.mp3", "03.Titel.mp3",
//                           "3 Titel.mp3",  "3-Titel.mp3",  "3.Titel.mp3"
//   Stufe 2 – Titelübereinstimmung (Fallback):
//     Falls keine Tracknummer-Datei gefunden, wird der Dateiname mit dem
//     Song-Titel verglichen (bereinigt: nur Buchstaben/Ziffern/Leerzeichen).
//     Suche ist bidirektional (Dateiname enthält Titel ODER Titel enthält Dateiname).
//
// Unterstützte Audioformate: MP3, FLAC, M4A/AAC, OGG, WAV
app.MapGet("/api/stream/{songId:int}", async (int songId) =>
{
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    // CDHeadPfad = Verzeichnispfad des Albums auf der Festplatte
    // CDSongTrackNum = Tracknummer (SmallInt → Convert.ToInt32)
    cmd.CommandText = @"
        SELECT RTRIM(ISNULL(h.CDHeadPfad,'')),
               ISNULL(s.CDSongTrackNum, 0),
               RTRIM(ISNULL(s.CDSongTitel,''))
        FROM   CDSong s
        JOIN   CDHead h ON s.CDHeadId = h.CDHeadId
        WHERE  s.CDSongId = @Id";
    cmd.Parameters.AddWithValue("@Id", songId);
    await using var r = await cmd.ExecuteReaderAsync();

    if (!await r.ReadAsync()) return Results.NotFound(new { error = "Song nicht gefunden" });

    var albumDir  = r.GetString(0).Trim();
    var trackNum  = r.IsDBNull(1) ? 0 : Convert.ToInt32(r[1]);
    var songTitle = r.GetString(2).Trim();
    await r.CloseAsync();

    if (string.IsNullOrEmpty(albumDir) || !Directory.Exists(albumDir))
        return Results.NotFound(new { error = "Album-Verzeichnis nicht gefunden" });

    var audioExts = new[] { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".wav" };
    string? filePath = null;

    // Stufe 1: Suche nach Tracknummer-Präfix
    if (trackNum > 0)
    {
        // Verschiedene Trennzeichen nach der Tracknummer abdecken
        // Zweistellig (03) und einstellig (3) da beides in der Praxis vorkommt
        var prefixes = new[]
        {
            $"{trackNum:D2} ", $"{trackNum:D2}-", $"{trackNum:D2}.",
            $"{trackNum} ",    $"{trackNum}-",    $"{trackNum}."
        };
        foreach (var ext in audioExts)
        {
            filePath = Directory.EnumerateFiles(albumDir, "*" + ext)
                .FirstOrDefault(f => prefixes.Any(p =>
                    Path.GetFileName(f).StartsWith(p, StringComparison.OrdinalIgnoreCase)));
            if (filePath != null) break;
        }
    }

    // Stufe 2: Fallback über Titelübereinstimmung
    // Bereinigung: Sonderzeichen entfernen damit "Let's Go" = "Lets Go" matcht
    if (filePath == null && !string.IsNullOrEmpty(songTitle))
    {
        var titleClean = new string(songTitle.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
        foreach (var ext in audioExts)
        {
            filePath = Directory.EnumerateFiles(albumDir, "*" + ext)
                .FirstOrDefault(f =>
                {
                    var fn = new string(Path.GetFileNameWithoutExtension(f).ToLowerInvariant()
                        .Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
                    return fn.Contains(titleClean) || titleClean.Contains(fn);
                });
            if (filePath != null) break;
        }
    }

    if (filePath == null)
        return Results.NotFound(new { error = $"Audio-Datei für Track {trackNum} nicht gefunden" });

    // Results.File mit enableRangeProcessing=true: ASP.NET Core verarbeitet
    // HTTP Range-Header automatisch und sendet nur den angeforderten Byte-Bereich.
    return Results.File(filePath, GetAudioMime(filePath), enableRangeProcessing: true);
});

// ── GET /api/cover/{albumId} ───────────────────────────────────────────────────
// Liefert das Cover-Bild eines Albums.
//
// Suchstrategie (drei Stufen):
//   1. CDHeadCoverPfad: Direkt hinterlegter Pfad in der Datenbank
//   2. Bekannte Standard-Dateinamen im Album-Verzeichnis (folder.jpg, cover.jpg, ...)
//   3. Beliebige erste .jpg oder .png Datei im Album-Verzeichnis
//
// HTTP 404 wenn kein Bild gefunden (das Frontend zeigt dann einen Platzhalter).
app.MapGet("/api/cover/{albumId:int}", async (int albumId) =>
{
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT RTRIM(ISNULL(CDHeadCoverPfad,'')),
               RTRIM(ISNULL(CDHeadPfad,''))
        FROM   CDHead WHERE CDHeadId = @Id";
    cmd.Parameters.AddWithValue("@Id", albumId);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) return Results.NotFound();

    var coverPath = r.GetString(0).Trim();
    var albumDir  = r.GetString(1).Trim();
    await r.CloseAsync();

    // Stufe 1: Expliziter Cover-Pfad in der DB
    if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath))
        return Results.File(coverPath, GetImageMime(coverPath));

    // Stufe 2 + 3: Im Album-Verzeichnis suchen
    if (!string.IsNullOrEmpty(albumDir) && Directory.Exists(albumDir))
    {
        // Bekannte Standard-Dateinamen (Reihenfolge = Priorität)
        foreach (var name in new[] { "folder.jpg","cover.jpg","front.jpg","albumart.jpg",
                                     "Folder.jpg","Cover.jpg","folder.png","cover.png" })
        {
            var p = Path.Combine(albumDir, name);
            if (File.Exists(p)) return Results.File(p, GetImageMime(p));
        }
        // Beliebiges Bild als letzter Ausweg
        var any = Directory.EnumerateFiles(albumDir, "*.jpg")
                           .Concat(Directory.EnumerateFiles(albumDir, "*.png"))
                           .FirstOrDefault();
        if (any != null) return Results.File(any, GetImageMime(any));
    }

    return Results.NotFound();
});

// ── GET /api/stats ─────────────────────────────────────────────────────────────
// Liefert Gesamtzahlen der Bibliothek für die Kopfzeile im Frontend.
// Wird beim App-Start einmalig aufgerufen.
// Rückgabe: { albums, songs, artists, genres }
app.MapGet("/api/stats", async () =>
{
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT
            (SELECT COUNT(*) FROM CDHead)    AS Albums,
            (SELECT COUNT(*) FROM CDSong)    AS Songs,
            (SELECT COUNT(*) FROM CDArtist)  AS Artists,
            (SELECT COUNT(DISTINCT RTRIM(CDHeadGenre)) FROM CDHead
             WHERE LTRIM(RTRIM(CDHeadGenre)) <> '') AS Genres";
    await using var r = await cmd.ExecuteReaderAsync();
    if (await r.ReadAsync())
        return Results.Json(new
        {
            albums  = r.GetInt32(0),
            songs   = r.GetInt32(1),
            artists = r.GetInt32(2),
            genres  = r.GetInt32(3),
        });
    return Results.Json(new { albums=0, songs=0, artists=0, genres=0 });
});

// ── GET /api/albums/by-year ────────────────────────────────────────────────────
// Spezialendpunkt für "Beste je Jahr" und "All Time Best" Ansichten.
//
// Parameter:
//   mode       "report" = All Time Best (nach Wertung sortiert, optional TOP N)
//              sonst    = Beste je Jahr (nach Jahr absteigend)
//   limit      Maximale Anzahl Ergebnisse (nur im report-Modus)
//   maxPerYear Maximale Alben pro Jahr (aktiviert ROW_NUMBER() PARTITION BY Jahr)
//   genres     Genre-Filter (komma-getrennt)
//   yearFrom   Frühestes Jahr
//   yearTo     Spätestes Jahr
//   maxRating  Maximale Wertung (Standard 2.25 = nur sehr gute Alben)
//   typ        Typ-Filter (Standard: nur Studio-Alben 'S')
//   cat        Kategorie-Filter (Standard: nur Originale 'O')
//
// Alben mit State='M' werden ausgeblendet (markierte/ausgeblendete Einträge).
//
// Wenn maxPerYear > 0: CTE mit ROW_NUMBER() für "Bestes Album je Jahr" Logik.
// Wenn maxPerYear = 0: Einfaches SELECT mit optionalem TOP.
app.MapGet("/api/albums/by-year", async (
    string?  mode,
    int?     limit,
    int?     maxPerYear,
    string?  genres,
    int?     yearFrom,
    int?     yearTo,
    double?  maxRating,
    string?  typ,
    string?  cat) =>
{
    bool   isReport = string.Equals(mode, "report", StringComparison.OrdinalIgnoreCase);
    double maxW     = (maxRating is > 0 and < 5.0) ? maxRating.Value : 2.25;
    int    topN     = (isReport && limit is > 0) ? limit.Value : 0;
    int    perYear  = maxPerYear is > 0 ? maxPerYear.Value : 0;

    // Typ-Bedingung: Standard = nur Studio-Alben
    string typCond;
    if (!string.IsNullOrWhiteSpace(typ))
    {
        var vals = typ.Split(',').Select(v => v.Trim().ToUpper())
                      .Where(v => v.Length == 1 && "SLM".Contains(v))
                      .Select(v => $"'{v}'").ToList();
        typCond = vals.Count > 0
            ? $"RTRIM(h.CDHeadTyp) IN ({string.Join(",", vals)})"
            : "RTRIM(h.CDHeadTyp) = 'S'";
    }
    else
    {
        typCond = "RTRIM(h.CDHeadTyp) = 'S'";
    }

    // Kategorie-Bedingung: Standard = nur Originale
    string catCond;
    if (!string.IsNullOrWhiteSpace(cat))
    {
        var vals = cat.Split(',').Select(v => v.Trim().ToUpper())
                      .Where(v => v.Length == 1 && "OSCMB".Contains(v))
                      .Select(v => $"'{v}'").ToList();
        catCond = vals.Count > 0
            ? $"RTRIM(h.CDHeadCat) IN ({string.Join(",", vals)})"
            : "RTRIM(h.CDHeadCat) = 'O'";
    }
    else
    {
        catCond = "RTRIM(h.CDHeadCat) = 'O'";
    }

    var where = new List<string>
    {
        typCond,
        catCond,
        "(h.CDHeadState IS NULL OR RTRIM(h.CDHeadState) <> 'M')",  // Ausgeblendete ignorieren
        "h.CDHeadWertung > 0",                                      // Unbewertete ignorieren
        "h.CDHeadWertung <= @MaxRating",
    };

    if (!string.IsNullOrWhiteSpace(genres))
    {
        var gl = genres.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (gl.Length == 1)
            where.Add("RTRIM(h.CDHeadGenre) LIKE '%' + @Genre0 + '%'");
        else
        {
            var likes = string.Join(" OR ", gl.Select((_, i) => $"RTRIM(h.CDHeadGenre) LIKE '%' + @Genre{i} + '%'"));
            where.Add($"({likes})");
        }
    }
    if (yearFrom is > 0) where.Add("h.CDHeadJahr >= @YearFrom");
    if (yearTo   is > 0) where.Add("h.CDHeadJahr <= @YearTo");

    var whereClause = "WHERE " + string.Join(" AND ", where);
    var topClause   = topN > 0 ? $"TOP ({topN})" : "";
    var orderClause = isReport
        ? "ORDER BY CDHeadWertung ASC"       // All Time Best: beste Wertung zuerst
        : "ORDER BY CDHeadJahr DESC, CDHeadWertung ASC";  // Je Jahr: neuestes zuerst

    string sql;
    if (perYear > 0)
    {
        // CTE-Variante: ROW_NUMBER() teilt die Ergebnismenge nach Jahr auf
        // und nummeriert innerhalb jedes Jahres nach Wertung (1 = beste).
        // Dann WHERE _rn <= @MaxPerYear filtert auf die besten N pro Jahr.
        sql = $@"
            WITH Ranked AS (
                SELECT h.CDHeadId,
                       RTRIM(a.CDArtistName)    AS ArtistName,
                       RTRIM(h.CDHeadTitel)     AS Titel,
                       h.CDHeadJahr,
                       RTRIM(h.CDHeadGenre)     AS Genre,
                       h.CDHeadWertung,
                       h.CDHeadTrackCount,
                       h.CDHeadTotalSec,
                       RTRIM(h.CDHeadCoverPfad) AS CoverPfad,
                       RTRIM(h.CDHeadTyp)       AS Typ,
                       RTRIM(h.CDLabelName)     AS Label,
                       h.CDHeadPlayed,
                       h.CDHeadAMG,
                       ROW_NUMBER() OVER (PARTITION BY h.CDHeadJahr ORDER BY h.CDHeadWertung ASC) AS _rn
                FROM   CDHead h
                JOIN   CDArtist a ON h.CDArtistId = a.CDArtistId
                {whereClause}
            )
            SELECT {topClause}
                   CDHeadId, ArtistName, Titel, CDHeadJahr, Genre,
                   CDHeadWertung, CDHeadTrackCount, CDHeadTotalSec,
                   CoverPfad, Typ, Label, CDHeadPlayed, CDHeadAMG
            FROM   Ranked
            WHERE  _rn <= @MaxPerYear
            {orderClause}";
    }
    else
    {
        // Einfaches SELECT ohne CTE
        sql = $@"
            SELECT {topClause}
                   h.CDHeadId,
                   RTRIM(a.CDArtistName)    AS ArtistName,
                   RTRIM(h.CDHeadTitel)     AS Titel,
                   h.CDHeadJahr,
                   RTRIM(h.CDHeadGenre)     AS Genre,
                   h.CDHeadWertung,
                   h.CDHeadTrackCount,
                   h.CDHeadTotalSec,
                   RTRIM(h.CDHeadCoverPfad) AS CoverPfad,
                   RTRIM(h.CDHeadTyp)       AS Typ,
                   RTRIM(h.CDLabelName)     AS Label,
                   h.CDHeadPlayed,
                   h.CDHeadAMG
            FROM   CDHead h
            JOIN   CDArtist a ON h.CDArtistId = a.CDArtistId
            {whereClause}
            {orderClause}";
    }

    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("@MaxRating", maxW);
    if (perYear > 0) cmd.Parameters.AddWithValue("@MaxPerYear", perYear);
    if (!string.IsNullOrWhiteSpace(genres))
    {
        var gl = genres.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < gl.Length; i++)
            cmd.Parameters.AddWithValue($"@Genre{i}", gl[i]);
    }
    if (yearFrom is > 0) cmd.Parameters.AddWithValue("@YearFrom", yearFrom.Value);
    if (yearTo   is > 0) cmd.Parameters.AddWithValue("@YearTo",   yearTo.Value);

    var albums = new List<object>();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        albums.Add(new
        {
            id         = Convert.ToInt32(r[0]),
            artist     = r.GetString(1),
            title      = r.GetString(2),
            year       = r.IsDBNull(3)  ? 0   : Convert.ToInt32(r[3]),
            genre      = r.IsDBNull(4)  ? ""  : r.GetString(4).Trim(),
            rating     = r.IsDBNull(5)  ? 0.0 : Convert.ToDouble(r[5]),
            trackCount = r.IsDBNull(6)  ? 0   : Convert.ToInt32(r[6]),
            totalSec   = r.IsDBNull(7)  ? 0   : Convert.ToInt32(r[7]),
            coverPath  = r.IsDBNull(8)  ? null: r.GetString(8).Trim(),
            typ        = r.IsDBNull(9)  ? ""  : r.GetString(9).Trim(),
            label      = r.IsDBNull(10) ? ""  : r.GetString(10).Trim(),
            played     = r.IsDBNull(11) ? 0   : Convert.ToInt32(r[11]),
            amg        = r.IsDBNull(12) ? 0.0 : Convert.ToDouble(r[12]),
        });
    }

    return Results.Json(new { total = albums.Count, albums });
});

// ── GET /api/playlists ─────────────────────────────────────────────────────────
// Liefert alle gespeicherten Playlists (aus PlaylistClaude-Tabelle).
// songFlag ist gesetzt, wenn die Playlist eine SmartRule mit SongFlag hat (z.B. HOF-Playlists).
app.MapGet("/api/playlists", async () =>
{
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT p.PlaylistId,
               RTRIM(p.PlaylistName) AS Name,
               (SELECT COUNT(*) FROM PlaylistDetailClaude d WHERE d.PlaylistId = p.PlaylistId) AS TrackCount,
               CAST(p.PlaylistSelect AS NVARCHAR(MAX)) AS PlaylistSelect
        FROM   PlaylistClaude p
        ORDER BY p.PlaylistName";
    var list = new List<object>();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        var ps = r.IsDBNull(3) ? null : r.GetString(3);
        string? songFlag    = null;
        string? genreGroups = null;
        if (ps != null)
        {
            var mf = System.Text.RegularExpressions.Regex.Match(ps, @"SongFlag=""([^""]+)""");
            if (mf.Success) songFlag = mf.Groups[1].Value;
            var mg = System.Text.RegularExpressions.Regex.Match(ps, @"GenreGroups=""([^""]+)""");
            if (mg.Success) genreGroups = mg.Groups[1].Value;
        }
        list.Add(new { id = r.GetInt32(0), name = r.GetString(1).Trim(), trackCount = r.GetInt32(2), songFlag, genreGroups });
    }
    return Results.Json(list);
});

// ── GET /api/playlists/{id}/tracks ────────────────────────────────────────────
// Liefert die Songs einer Playlist mit Metadaten für Player und Queue.
app.MapGet("/api/playlists/{id:int}/tracks", async (int id) =>
{
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT d.CDSongId,
               d.SongNr,
               RTRIM(ISNULL(d.CDSongTitel,''))    AS Title,
               RTRIM(ISNULL(d.CDArtistSort,''))   AS Artist,
               RTRIM(ISNULL(d.CDHeadTitel,''))     AS Album,
               ISNULL(d.CDSongSecs, 0)            AS Secs,
               ISNULL(d.CDSongNote, 0)             AS Note,
               ISNULL(d.CDHeadJahr, 0)             AS Year,
               s.CDHeadId
        FROM   PlaylistDetailClaude d
        JOIN   CDSong s ON d.CDSongId = s.CDSongId
        WHERE  d.PlaylistId = @Id
        ORDER BY d.SongNr";
    cmd.Parameters.AddWithValue("@Id", id);
    var tracks = new List<object>();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
        tracks.Add(new
        {
            songId  = Convert.ToInt32(r[0]),
            songNr  = Convert.ToInt32(r[1]),
            title   = r.GetString(2).Trim(),
            artist  = r.GetString(3).Trim(),
            album   = r.GetString(4).Trim(),
            secs    = Convert.ToInt32(r[5]),
            note    = Convert.ToInt32(r[6]),
            year    = Convert.ToInt32(r[7]),
            albumId = r.IsDBNull(8) ? 0 : Convert.ToInt32(r[8]),
        });
    return Results.Json(tracks);
});

// ── GET /api/generator/songs ──────────────────────────────────────────────────
// Mini-Playlist-Generator: alle Songs eines Künstlers nach Typ/Modus.
// Parameter:
//   artist              – exakter Künstlername (optional wenn genres gesetzt)
//   genres              – kommagetrennte Genre-Namen (OR-verknüpft, optional wenn artist gesetzt)
//   headTyp             – 'S'=Studio (Standard), 'L'=Live
//   includeCompilations – auch CDHeadCat='C' (Sampler/Best Of) einschließen
//   maxNote             – 0=alle, 1=nur Note 1, 2=≤Note 2, 3=≤Note 3
//   minNote             – 0=kein Filter, 2=Note ≥ 2 (kombiniert mit maxNote für exakt)
//   played              – 0=alle, 1=nie gespielt (CDSongCount=0), 2=bereits gespielt
//   maxSongs            – 0=alle, >0=zufällige Auswahl auf max. N Songs begrenzt
// Artist und Genre sind OR-verknüpft: beide können gleichzeitig gesetzt sein.
// Rückgabeformat identisch mit /api/playlists/{id}/tracks.
app.MapGet("/api/generator/songs", async (string? artist, string? genres, string? headTyp, bool? includeCompilations, int? maxNote, int? minNote, int? played, int? maxSongs) =>
{
    bool hasArtist = !string.IsNullOrWhiteSpace(artist);
    var  genreList = (genres ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    bool hasGenres = genreList.Length > 0;
    if (!hasArtist && !hasGenres)
        return Results.BadRequest(new { error = "artist oder genres erforderlich" });

    bool   inclComp = includeCompilations ?? false;
    int    maxN     = maxNote ?? 0;
    int    minN     = minNote ?? 0;
    string typ      = (headTyp ?? "S").Trim().ToUpper() == "L" ? "L" : "S";

    string catFilter    = inclComp
        ? "LTRIM(RTRIM(ISNULL(h.CDHeadCat,''))) IN ('O','C')"
        : "LTRIM(RTRIM(ISNULL(h.CDHeadCat,''))) = 'O'";
    string noteFilter    = maxN > 0 ? "AND ISNULL(s.CDSongNote,0) <= @MaxNote" : "";
    string noteMinFilter = minN > 0 ? "AND ISNULL(s.CDSongNote,0) >= @MinNote" : "";
    string playedFilter  = (played ?? 0) switch {
        1 => "AND ISNULL(s.CDSongCount, 0) = 0",
        2 => "AND ISNULL(s.CDSongCount, 0) > 0",
        _ => ""
    };

    // OR-Bedingung: Artist ODER Genres
    var orParts = new List<string>();
    if (hasArtist) orParts.Add("(RTRIM(a.CDArtistName) = @Artist OR RTRIM(a.CDArtistSort) = @Artist)");
    if (hasGenres)
    {
        var genreConds = genreList.Select((_, i) => $"RTRIM(h.CDHeadGenre) LIKE '%' + @Genre{i} + '%'");
        orParts.Add($"({string.Join(" OR ", genreConds)})");
    }
    string sourceFilter = string.Join(" OR ", orParts);

    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $@"
        SELECT s.CDSongId,
               RTRIM(ISNULL(s.CDSongTitel,''))  AS Title,
               RTRIM(ISNULL(a.CDArtistName,'')) AS Artist,
               RTRIM(ISNULL(h.CDHeadTitel,''))  AS Album,
               ISNULL(s.CDSongSecs, 0)          AS Secs,
               ISNULL(CONVERT(INT, s.CDSongNote), 0) AS Note,
               ISNULL(h.CDHeadJahr, 0)          AS Year,
               h.CDHeadId,
               ISNULL(CONVERT(INT, s.CDSongTrackNum), 0) AS TrackNum
        FROM   CDSong   s
        JOIN   CDHead   h  ON s.CDHeadId   = h.CDHeadId
        JOIN   CDArtist a  ON h.CDArtistId = a.CDArtistId
        WHERE  ({sourceFilter})
        AND    LTRIM(RTRIM(ISNULL(h.CDHeadTyp,''))) = @HeadTyp
        AND    {catFilter}
        {noteFilter}
        {noteMinFilter}
        {playedFilter}
        ORDER BY h.CDHeadJahr, h.CDHeadTitel, s.CDSongTrackNum";

    if (hasArtist) cmd.Parameters.AddWithValue("@Artist", artist!.Trim());
    for (int i = 0; i < genreList.Length; i++)
        cmd.Parameters.AddWithValue($"@Genre{i}", genreList[i]);
    cmd.Parameters.AddWithValue("@HeadTyp", typ);
    if (maxN > 0) cmd.Parameters.AddWithValue("@MaxNote", maxN);
    if (minN > 0) cmd.Parameters.AddWithValue("@MinNote", minN);

    var tracks = new List<object>();
    int nr = 1;
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
        tracks.Add(new
        {
            songId   = Convert.ToInt32(r[0]),
            songNr   = nr++,
            title    = r.GetString(1).Trim(),
            artist   = r.GetString(2).Trim(),
            album    = r.GetString(3).Trim(),
            secs     = Convert.ToInt32(r[4]),
            note     = Convert.ToInt32(r[5]),
            year     = Convert.ToInt32(r[6]),
            albumId  = r.IsDBNull(7) ? 0 : Convert.ToInt32(r[7]),
            trackNum = Convert.ToInt32(r[8]),
        });

    // Zufällige Auswahl wenn maxSongs gesetzt
    int limit = maxSongs ?? 0;
    if (limit > 0 && tracks.Count > limit)
    {
        var rng = new Random();
        tracks = tracks.OrderBy(_ => rng.Next()).Take(limit).ToList();
    }

    return Results.Json(tracks);
});

// ════════════════════════════════════════════════════════════════════════════════
// NEUBEWERTUNG (Vorschläge) + FERNSTEUERUNG (iPhone / Gamepad via Relay)
// ════════════════════════════════════════════════════════════════════════════════
// Grundprinzip: Player/Handy schreiben NIE an CDSongNote – nur Vorschläge in
// CDSongReRate. Übernommen wird später bewusst in NextYAMSKopie.

// In-Memory-Zustand der Fernsteuerung (Einzelhaushalt: 1 Player + n Fernbedienungen).
var remote = new RemoteHub();
// Serialisiert Note-Schritte, damit schnelle Doppeldrücke (3→2→1) sich nicht überholen.
var reRateGate = new SemaphoreSlim(1, 1);

// Hintergrund-Playlist "Background PL": Smart-Playlist mit PlaylistTag="$BGPL$".
// Mitgliedschaft wird über das Song-Flag $BGPL$ in CDSong.CDSongFlags bestimmt
// (Regel SongFlag="$BGPL$"). BGPL +/- setzt/entfernt dieses Flag UND hält die
// materialisierte PlaylistDetailClaude-Tabelle synchron, damit es sofort wirkt.
const string BgplTag = "$BGPL$";
int? bgplPlaylistId = null;

// Schreibt einen ABSOLUTEN Notenvorschlag (1..5) für einen Song und merkt dessen Album
// vor. Upsert: max. ein offener Vorschlag je Song. Liefert den gespeicherten Wert, oder
// null wenn der Song unbekannt ist.
async Task<int?> ApplyProposalAsync(int songId, int note)
{
    if (note < 1) note = 1;
    if (note > 5) note = 5;

    await using var conn = NewConn();
    await conn.OpenAsync();

    await using var look = conn.CreateCommand();
    look.CommandText = "SELECT CDHeadId FROM CDSong WHERE CDSongId = @s";
    look.Parameters.AddWithValue("@s", songId);
    var headObj = await look.ExecuteScalarAsync();
    if (headObj is null || headObj is DBNull) return null;

    await using var up = conn.CreateCommand();
    up.CommandText = @"
        UPDATE CDSongReRate
        SET    NoteNeu = @n, CreatedAt = SYSDATETIME()
        WHERE  CDSongId = @s AND ProcessedAt IS NULL;
        IF @@ROWCOUNT = 0
            INSERT INTO CDSongReRate (CDSongId, CDHeadId, NoteNeu)
            VALUES (@s, @h, @n);";
    up.Parameters.AddWithValue("@s", songId);
    up.Parameters.AddWithValue("@h", Convert.ToInt32(headObj));
    up.Parameters.AddWithValue("@n", note);
    await up.ExecuteNonQueryAsync();
    return note;
}

// Aktuell wirksamer Notenwert eines Songs: offener Vorschlag, sonst gespeicherte
// CDSongNote, sonst 3 (unbewertet). Basis der ▲/▼-Schrittlogik.
async Task<int> EffectiveNoteAsync(int songId)
{
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT COALESCE(
                 (SELECT TOP 1 NoteNeu FROM CDSongReRate
                  WHERE CDSongId = @s AND ProcessedAt IS NULL),
                 NULLIF((SELECT CDSongNote FROM CDSong WHERE CDSongId = @s), 0),
                 3)";
    cmd.Parameters.AddWithValue("@s", songId);
    var v = await cmd.ExecuteScalarAsync();
    return v is null || v is DBNull ? 3 : Convert.ToInt32(v);
}

// Aktueller offener Vorschlag eines Songs (oder null).
async Task<int?> OpenProposalAsync(int songId)
{
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT TOP 1 NoteNeu FROM CDSongReRate WHERE CDSongId = @s AND ProcessedAt IS NULL";
    cmd.Parameters.AddWithValue("@s", songId);
    var v = await cmd.ExecuteScalarAsync();
    return v is null || v is DBNull ? (int?)null : Convert.ToInt32(v);
}

// Löst die BGPL-Playlist (PlaylistTag="$BGPL$") einmalig auf und cacht ihre Id.
async Task<int?> ResolveBgplPlaylistIdAsync()
{
    if (bgplPlaylistId is { } cached) return cached;
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText =
        "SELECT TOP 1 PlaylistId FROM PlaylistClaude " +
        "WHERE CAST(PlaylistSelect AS NVARCHAR(MAX)) LIKE '%PlaylistTag=\"' + @tag + '\"%'";
    cmd.Parameters.AddWithValue("@tag", BgplTag);
    var v = await cmd.ExecuteScalarAsync();
    if (v is null || v is DBNull) return null;
    bgplPlaylistId = Convert.ToInt32(v);
    return bgplPlaylistId;
}

// Ist der Song aktuell in der BGPL (Flag $BGPL$ in CDSongFlags)?
async Task<bool> IsInBgplAsync(int songId)
{
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText =
        "SELECT CASE WHEN EXISTS (SELECT 1 FROM CDSong " +
        "WHERE CDSongId = @s AND CDSongFlags LIKE '%' + @tag + '%') THEN 1 ELSE 0 END";
    cmd.Parameters.AddWithValue("@s", songId);
    cmd.Parameters.AddWithValue("@tag", BgplTag);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
}

// Liefert den rohen CDSongFlags-String eines Songs (z.B. "$BGPL$$HOFBL$"), oder null.
async Task<string?> GetSongFlagsAsync(int songId)
{
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT CDSongFlags FROM CDSong WHERE CDSongId=@s";
    cmd.Parameters.AddWithValue("@s", songId);
    var v = await cmd.ExecuteScalarAsync();
    return v is null || v is DBNull ? null : v.ToString();
}

// Liefert die CDGenreGroup des Albums zum Song, oder null wenn nicht zugeordnet.
async Task<string?> GetSongGenreGroupAsync(int songId)
{
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT TOP 1 RTRIM(ISNULL(g.CDGenreGroup, ''))
        FROM   CDSong  s
        JOIN   CDHead  h ON s.CDHeadId = h.CDHeadId
        LEFT JOIN CDGenre g ON RTRIM(h.CDHeadGenre) = RTRIM(g.CDGenreName)
        WHERE  s.CDSongId = @s";
    cmd.Parameters.AddWithValue("@s", songId);
    var v = await cmd.ExecuteScalarAsync();
    return v is null || v is DBNull ? null : v.ToString()?.Trim();
}

// Fügt den Song zur BGPL hinzu: Flag setzen + Detail-Zeile anlegen (atomar). Liefert
// true (jetzt Mitglied), oder null wenn Song oder Playlist nicht existieren.
async Task<bool?> BgplAddAsync(int songId)
{
    var plId = await ResolveBgplPlaylistIdAsync();
    if (plId is null) return null;

    await using var conn = NewConn();
    await conn.OpenAsync();

    await using var look = conn.CreateCommand();
    look.CommandText = "SELECT 1 FROM CDSong WHERE CDSongId = @s";
    look.Parameters.AddWithValue("@s", songId);
    if (await look.ExecuteScalarAsync() is null) return null;

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SET XACT_ABORT ON;
        BEGIN TRAN;
          UPDATE CDSong
             SET CDSongFlags = RTRIM(ISNULL(CDSongFlags, '')) + @tag
           WHERE CDSongId = @s
             AND (CDSongFlags IS NULL OR CDSongFlags NOT LIKE '%' + @tag + '%');

          IF NOT EXISTS (SELECT 1 FROM PlaylistDetailClaude WHERE PlaylistId = @pl AND CDSongId = @s)
            INSERT INTO PlaylistDetailClaude
                (PlaylistId, CDSongId, SongNr, SelectedSong, CDArtistSort, CDSongTitel, CDSongNote,
                 CDSongDauer, CDSongJahr, CDHeadTitel, CDHeadJahr, CDHeadWertung, CDSongAppleHigh,
                 CDSongAppleLow, CDSongSecs, RuleName, CDHeadId, CDHeadAMG)
            SELECT @pl, s.CDSongId,
                   (SELECT ISNULL(MAX(SongNr), 0) + 1 FROM PlaylistDetailClaude WHERE PlaylistId = @pl),
                   1, RTRIM(ISNULL(a.CDArtistSort, '')), s.CDSongTitel, s.CDSongNote, s.CDSongDauer,
                   s.CDSongJahr, h.CDHeadTitel, h.CDHeadJahr, h.CDHeadWertung, s.CDSongAppleHigh,
                   s.CDSongAppleLow, s.CDSongSecs, NULL, s.CDHeadId, h.CDHeadAMG
            FROM CDSong s
            JOIN CDHead h ON h.CDHeadId = s.CDHeadId
            LEFT JOIN CDArtist a ON a.CDArtistId = s.SongArtistId
            WHERE s.CDSongId = @s;
        COMMIT;";
    cmd.Parameters.AddWithValue("@s", songId);
    cmd.Parameters.AddWithValue("@pl", plId.Value);
    cmd.Parameters.AddWithValue("@tag", BgplTag);
    await cmd.ExecuteNonQueryAsync();
    return true;
}

// Entfernt den Song aus der BGPL: Flag entfernen + Detail-Zeile löschen (atomar).
// Liefert false (nicht mehr Mitglied), oder null wenn Song/Playlist nicht existieren.
async Task<bool?> BgplRemoveAsync(int songId)
{
    var plId = await ResolveBgplPlaylistIdAsync();
    if (plId is null) return null;

    await using var conn = NewConn();
    await conn.OpenAsync();

    await using var look = conn.CreateCommand();
    look.CommandText = "SELECT 1 FROM CDSong WHERE CDSongId = @s";
    look.Parameters.AddWithValue("@s", songId);
    if (await look.ExecuteScalarAsync() is null) return null;

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SET XACT_ABORT ON;
        BEGIN TRAN;
          UPDATE CDSong SET CDSongFlags = RTRIM(REPLACE(ISNULL(CDSongFlags, ''), @tag, '')) WHERE CDSongId = @s;
          DELETE FROM PlaylistDetailClaude WHERE PlaylistId = @pl AND CDSongId = @s;
        COMMIT;";
    cmd.Parameters.AddWithValue("@s", songId);
    cmd.Parameters.AddWithValue("@pl", plId.Value);
    cmd.Parameters.AddWithValue("@tag", BgplTag);
    await cmd.ExecuteNonQueryAsync();
    return false;
}

// ── POST /api/songs/{id}/rerate  { note: 1..5 } ─────────────────────────────────
// Absoluter Notenvorschlag (z. B. 1–5-Taste direkt am Player).
app.MapPost("/api/songs/{songId:int}/rerate", async (int songId, ReRateDto body) =>
{
    var stored = await ApplyProposalAsync(songId, body.Note);
    return stored is null ? Results.NotFound() : Results.Json(new { songId, note = stored });
});

// ── DELETE /api/songs/{id}/rerate ───────────────────────────────────────────────
// Nimmt den offenen Vorschlag eines Songs zurück.
app.MapDelete("/api/songs/{songId:int}/rerate", async (int songId) =>
{
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM CDSongReRate WHERE CDSongId = @s AND ProcessedAt IS NULL";
    cmd.Parameters.AddWithValue("@s", songId);
    var n = await cmd.ExecuteNonQueryAsync();
    return Results.Json(new { songId, removed = n });
});

// ── GET /api/rerate ─────────────────────────────────────────────────────────────
// Offene Vorschläge (optional für eine Player-Liste).
app.MapGet("/api/rerate", async () =>
{
    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT r.CDSongId, r.CDHeadId, r.NoteNeu, r.CreatedAt,
               RTRIM(ISNULL(s.CDSongTitel,'')) AS Titel,
               RTRIM(ISNULL(h.CDHeadTitel,''))  AS AlbumTitel,
               ISNULL(s.CDSongNote, 0)          AS NoteAlt
        FROM   CDSongReRate r
        JOIN   CDSong s ON r.CDSongId = s.CDSongId
        JOIN   CDHead h ON r.CDHeadId = h.CDHeadId
        WHERE  r.ProcessedAt IS NULL
        ORDER  BY r.CreatedAt DESC";
    var items = new List<object>();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
        items.Add(new
        {
            songId     = Convert.ToInt32(r[0]),
            albumId    = Convert.ToInt32(r[1]),
            noteNeu    = Convert.ToInt32(r[2]),
            createdAt  = r.GetDateTime(3).ToString("s"),
            title      = r.GetString(4).Trim(),
            albumTitle = r.GetString(5).Trim(),
            noteAlt    = Convert.ToInt32(r[6]),
        });
    return Results.Json(items);
});

// ── POST /api/songs/{id}/bgpl  /  DELETE /api/songs/{id}/bgpl ────────────────────
// Fügt den Song zur Hintergrund-Playlist $BGPL$ hinzu bzw. entfernt ihn
// (Flag in CDSongFlags + Zeile in PlaylistDetailClaude, synchron).
app.MapPost("/api/songs/{songId:int}/bgpl", async (int songId) =>
{
    var r = await BgplAddAsync(songId);
    return r is null ? Results.NotFound() : Results.Json(new { songId, bgpl = r.Value });
});
app.MapDelete("/api/songs/{songId:int}/bgpl", async (int songId) =>
{
    var r = await BgplRemoveAsync(songId);
    return r is null ? Results.NotFound() : Results.Json(new { songId, bgpl = r.Value });
});

// ── POST /api/songs/{id}/set-flag ────────────────────────────────────────────────
// Setzt oder entfernt ein beliebiges Flag in CDSongFlags (z.B. $HOFBL$).
// Für HOF-SmartRule-Playlists: kein PlaylistDetailClaude-Eintrag nötig, die SmartRule
// wertet CDSongFlags direkt aus.
app.MapPost("/api/songs/{songId:int}/set-flag", async (int songId, SetFlagDto body) =>
{
    if (string.IsNullOrWhiteSpace(body.Flag)) return Results.BadRequest(new { error = "Flag fehlt" });
    var tag = body.Flag.Trim();

    // Genre-Validierung beim Hinzufügen: Song muss zur Genre-Gruppe der Playlist passen.
    if (!body.Remove && body.PlaylistId.HasValue)
    {
        // GenreGroups der Playlist aus PlaylistSelect-XML lesen
        string? playlistGenreGroups = null;
        await using (var connP = NewConn())
        {
            await connP.OpenAsync();
            await using var cmdP = connP.CreateCommand();
            cmdP.CommandText = "SELECT CAST(PlaylistSelect AS NVARCHAR(MAX)) FROM PlaylistClaude WHERE PlaylistId=@p";
            cmdP.Parameters.AddWithValue("@p", body.PlaylistId.Value);
            var pv = await cmdP.ExecuteScalarAsync();
            if (pv is not null && pv is not DBNull)
            {
                var mgg = System.Text.RegularExpressions.Regex.Match(pv.ToString()!, @"GenreGroups=""([^""]+)""");
                if (mgg.Success) playlistGenreGroups = mgg.Groups[1].Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(playlistGenreGroups))
        {
            var songGenreGroup = await GetSongGenreGroupAsync(songId);
            var allowed = playlistGenreGroups
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool match = !string.IsNullOrWhiteSpace(songGenreGroup) &&
                         allowed.Any(g => string.Equals(g, songGenreGroup, StringComparison.OrdinalIgnoreCase));
            if (!match)
                return Results.UnprocessableEntity(new
                {
                    error    = "Genre passt nicht",
                    songGenreGroup,
                    required = playlistGenreGroups
                });
        }
    }

    await using var conn = NewConn();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    if (body.Remove)
        cmd.CommandText = "UPDATE CDSong SET CDSongFlags = RTRIM(REPLACE(ISNULL(CDSongFlags,''),@tag,'')) WHERE CDSongId=@s";
    else
        cmd.CommandText = "UPDATE CDSong SET CDSongFlags = RTRIM(ISNULL(CDSongFlags,''))+@tag WHERE CDSongId=@s AND (CDSongFlags IS NULL OR CDSongFlags NOT LIKE '%'+@tag+'%')";
    cmd.Parameters.AddWithValue("@s", songId);
    cmd.Parameters.AddWithValue("@tag", tag);
    await cmd.ExecuteNonQueryAsync();
    return Results.Ok(new { songId, flag = tag, removed = body.Remove });
});

// ── Relay: Player ⇄ Fernbedienung (Short-Poll) ───────────────────────────────────
// Player meldet "läuft gerade".
app.MapPost("/api/remote/nowplaying", (NowPlayingDto np) =>
{
    remote.NowPlaying   = np;
    remote.NowPlayingAt = DateTime.UtcNow;
    return Results.Ok();
});

// Player holt anstehende Transport-Kommandos ab (und leert die Queue).
app.MapGet("/api/remote/poll", async () =>
{
    var cmds = new List<string>();
    while (remote.Commands.TryDequeue(out var c)) cmds.Add(c);
    var np = remote.NowPlaying;
    int? proposal = (np is not null && np.SongId > 0) ? await OpenProposalAsync(np.SongId) : null;
    bool bgpl     = (np is not null && np.SongId > 0) && await IsInBgplAsync(np.SongId);
    return Results.Json(new { commands = cmds, proposal, bgpl });
});

// Fernbedienung liest aktuellen Zustand + offenen Vorschlag.
app.MapGet("/api/remote/state", async () =>
{
    var np = remote.NowPlaying;
    int? proposal  = (np is not null && np.SongId > 0) ? await OpenProposalAsync(np.SongId) : null;
    bool bgpl      = (np is not null && np.SongId > 0) && await IsInBgplAsync(np.SongId);
    string? sFlags      = (np is not null && np.SongId > 0) ? await GetSongFlagsAsync(np.SongId)      : null;
    string? sGenreGroup = (np is not null && np.SongId > 0) ? await GetSongGenreGroupAsync(np.SongId) : null;
    var online = (DateTime.UtcNow - remote.NowPlayingAt).TotalSeconds < 10;
    return Results.Json(new { online, nowPlaying = np, proposal, bgpl, songFlags = sFlags, songGenreGroup = sGenreGroup });
});

// Fernbedienung sendet ein Kommando. Transport → Queue für den Player.
// Note → Server schreibt Vorschlag für den laufenden Song (absolut oder ±1).
app.MapPost("/api/remote/command", async (CommandDto body) =>
{
    var cmd = (body.Cmd ?? "").Trim().ToLowerInvariant();

    if (cmd is "next" or "prev" or "playpause" or "pause" or "stop"
        || (cmd.StartsWith("seek:") && double.TryParse(cmd.AsSpan(5), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)))
    {
        remote.Commands.Enqueue(cmd);
        return Results.Ok();
    }

    var np = remote.NowPlaying;
    if (np is null || np.SongId <= 0) return Results.Json(new { error = "kein laufender Song" });

    await reRateGate.WaitAsync();
    try
    {
        if (cmd == "bgpl:add")    { var b = await BgplAddAsync(np.SongId);    return Results.Json(new { songId = np.SongId, bgpl = b }); }
        if (cmd == "bgpl:remove") { var b = await BgplRemoveAsync(np.SongId); return Results.Json(new { songId = np.SongId, bgpl = b }); }

        int target;
        if (cmd == "note:up")        target = await EffectiveNoteAsync(np.SongId) - 1; // besser
        else if (cmd == "note:down") target = await EffectiveNoteAsync(np.SongId) + 1; // schlechter
        else if (cmd.StartsWith("note:set:") && int.TryParse(cmd.AsSpan(9), out var n)) target = n;
        else return Results.Json(new { error = "unbekanntes Kommando" });

        var stored = await ApplyProposalAsync(np.SongId, target);
        return Results.Json(new { songId = np.SongId, note = stored });
    }
    finally { reRateGate.Release(); }
});

// ── GET /api/statistics ──────────────────────────────────────────────────────────
// Aggregierte Kennzahlen für die Statistik-Seite (/stats): Qualität (Noten/Wertung),
// Zeit (Jahrzehnte), Interpreten, Genre/Subgenre, Hörverhalten, Datenqualität.
app.MapGet("/api/statistics", async (string? cat, string? typ, string[]? ig, string[]? xg) =>
{
    await using var conn = NewConn();
    await conn.OpenAsync();

    double Dbl(object v) => v is null || v is DBNull ? 0 : Convert.ToDouble(v);
    int    Int(object v) => v is null || v is DBNull ? 0 : Convert.ToInt32(v);
    string Str(object v) => v is null || v is DBNull ? "" : v.ToString().Trim();

    // Filter: erlaubte Album-Kategorien (CDHeadCat) und -Typen (CDHeadTyp). Leer oder
    // alle = kein Filter. Werte werden gegen Whitelists geprüft (keine SQL-Injection).
    var validCat = new HashSet<string> { "O", "C", "M", "S", "B" };
    var validTyp = new HashSet<string> { "S", "L", "M" };
    string? In(string? csv, HashSet<string> valid)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var p = csv.Split(',').Select(x => x.Trim().ToUpperInvariant()).Where(valid.Contains).Distinct().ToList();
        if (p.Count == 0 || p.Count == valid.Count) return null;
        return string.Join(",", p.Select(x => "'" + x + "'"));
    }
    var catIn = In(cat, validCat);
    var typIn = In(typ, validTyp);
    string? igIn = null, xgIn = null;  // ein-/ausgeschlossene Genres – unten gegen die echte Genre-Liste validiert
    string F(string a)
    {
        var p = new List<string>();
        if (catIn != null) p.Add($"RTRIM({a}.CDHeadCat) IN ({catIn})");
        if (typIn != null) p.Add($"RTRIM({a}.CDHeadTyp) IN ({typIn})");
        if (igIn  != null) p.Add($"RTRIM({a}.CDHeadGenre) IN ({igIn})");
        if (xgIn  != null) p.Add($"RTRIM({a}.CDHeadGenre) NOT IN ({xgIn})");
        return p.Count == 0 ? "" : " AND " + string.Join(" AND ", p);
    }

    async Task<List<object>> Q(string sql, Func<Microsoft.Data.SqlClient.SqlDataReader, object> map)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var list = new List<object>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(map(r));
        return list;
    }
    async Task<object> Row(string sql, Func<Microsoft.Data.SqlClient.SqlDataReader, object> map)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? map(r) : null;
    }

    // Filter-Optionen (ungefiltert, damit die Auswahl stabil bleibt)
    var facetsCat = await Q("SELECT RTRIM(CDHeadCat), COUNT(*) FROM CDHead WHERE RTRIM(ISNULL(CDHeadCat,''))<>'' GROUP BY RTRIM(CDHeadCat) ORDER BY 2 DESC",
        r => new { code = Str(r[0]), albums = Int(r[1]) });
    var facetsTyp = await Q("SELECT RTRIM(CDHeadTyp), COUNT(*) FROM CDHead WHERE RTRIM(ISNULL(CDHeadTyp,''))<>'' GROUP BY RTRIM(CDHeadTyp) ORDER BY 2 DESC",
        r => new { code = Str(r[0]), albums = Int(r[1]) });

    // Alle Genres (für die Filter-Liste) + Whitelist zum Validieren der Ausschlüsse.
    var facetsGenre = new List<object>();
    var genreSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT RTRIM(CDHeadGenre), COUNT(*) FROM CDHead WHERE RTRIM(ISNULL(CDHeadGenre,''))<>'' GROUP BY RTRIM(CDHeadGenre) ORDER BY 2 DESC";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) { var g = Str(r[0]); genreSet.Add(g); facetsGenre.Add(new { code = g, albums = Int(r[1]) }); }
    }
    string? GenreIn(string[]? arr)
    {
        if (arr == null || arr.Length == 0) return null;
        var picked = arr.Select(x => (x ?? "").Trim()).Where(x => x.Length > 0 && genreSet.Contains(x)).Distinct().ToList();
        return picked.Count > 0 ? string.Join(",", picked.Select(g => "'" + g.Replace("'", "''") + "'")) : null;
    }
    igIn = GenreIn(ig);
    xgIn = GenreIn(xg);

    // 2 — Qualität
    var noteHist = await Q(
        $"SELECT s.CDSongNote, COUNT(*) FROM CDSong s JOIN CDHead h ON h.CDHeadId=s.CDHeadId WHERE s.CDSongOrg='T' AND s.CDSongNote BETWEEN 1 AND 5{F("h")} GROUP BY s.CDSongNote ORDER BY s.CDSongNote",
        r => new { note = Int(r[0]), count = Int(r[1]) });
    var wertungHist = await Q(
        $"SELECT CAST(ROUND(h.CDHeadWertung,0) AS INT), COUNT(*) FROM CDHead h WHERE h.CDHeadWertung>0 AND h.CDHeadWertung<=5{F("h")} GROUP BY CAST(ROUND(h.CDHeadWertung,0) AS INT) ORDER BY 1",
        r => new { wertung = Int(r[0]), albums = Int(r[1]) });
    var quality = await Row(
        $"SELECT AVG(CAST(h.CDHeadSongs AS float)), AVG(CAST(h.CDHeadWert AS float)), AVG(h.CDHeadAMG), AVG(CAST(h.CDHeadCover AS float)), AVG(h.CDHeadWertung), (SELECT AVG(CAST(s.CDSongNote AS float)) FROM CDSong s JOIN CDHead h2 ON h2.CDHeadId=s.CDHeadId WHERE s.CDSongOrg='T' AND s.CDSongNote BETWEEN 1 AND 5{F("h2")}) FROM CDHead h WHERE h.CDHeadWertung>0{F("h")}",
        r => new { d1 = Math.Round(Dbl(r[0]),2), wert = Math.Round(Dbl(r[1]),2), amg = Math.Round(Dbl(r[2]),2), cover = Math.Round(Dbl(r[3]),2), wertung = Math.Round(Dbl(r[4]),2), avgNote = Math.Round(Dbl(r[5]),2) });

    // 3 — Zeit
    var perDecadeAlbums = await Q(
        $"SELECT (h.CDHeadJahr/10)*10, COUNT(*), AVG(h.CDHeadWertung) FROM CDHead h WHERE h.CDHeadJahr>=1940 AND h.CDHeadWertung>0{F("h")} GROUP BY (h.CDHeadJahr/10)*10 ORDER BY 1",
        r => new { decade = Int(r[0]), albums = Int(r[1]), avgWertung = Math.Round(Dbl(r[2]),2) });
    var perDecadeSongs = await Q(
        $"SELECT (s.CDSongJahr/10)*10, COUNT(*), AVG(CAST(s.CDSongNote AS float)) FROM CDSong s JOIN CDHead h ON h.CDHeadId=s.CDHeadId WHERE s.CDSongOrg='T' AND s.CDSongJahr>=1940 AND s.CDSongNote BETWEEN 1 AND 5{F("h")} GROUP BY (s.CDSongJahr/10)*10 ORDER BY 1",
        r => new { decade = Int(r[0]), songs = Int(r[1]), avgNote = Math.Round(Dbl(r[2]),2) });

    // 4 — Interpreten
    var topArtistsByAlbums = await Q(
        $"SELECT TOP 15 RTRIM(a.CDArtistSort), COUNT(*) FROM CDHead h JOIN CDArtist a ON a.CDArtistId=h.CDArtistId WHERE 1=1{F("h")} GROUP BY RTRIM(a.CDArtistSort) ORDER BY 2 DESC",
        r => new { artist = Str(r[0]), albums = Int(r[1]) });
    var topArtistsByNote = await Q(
        $"SELECT TOP 15 RTRIM(a.CDArtistSort), AVG(CAST(s.CDSongNote AS float)), COUNT(*) FROM CDSong s JOIN CDArtist a ON a.CDArtistId=s.CDArtistId JOIN CDHead h ON h.CDHeadId=s.CDHeadId WHERE s.CDSongOrg='T' AND s.CDSongNote BETWEEN 1 AND 5{F("h")} GROUP BY RTRIM(a.CDArtistSort) HAVING COUNT(*)>=40 ORDER BY 2 ASC",
        r => new { artist = Str(r[0]), avgNote = Math.Round(Dbl(r[1]),2), songs = Int(r[2]) });

    // 5 — Genre / Subgenre (Style)
    var topGenres = await Q(
        $"SELECT TOP 15 RTRIM(h.CDHeadGenre), COUNT(*), AVG(h.CDHeadWertung) FROM CDHead h WHERE RTRIM(ISNULL(h.CDHeadGenre,''))<>'' AND h.CDHeadWertung>0{F("h")} GROUP BY RTRIM(h.CDHeadGenre) ORDER BY 2 DESC",
        r => new { genre = Str(r[0]), albums = Int(r[1]), avgWertung = Math.Round(Dbl(r[2]),2) });
    var styles = await Q(
        $"SELECT RTRIM(s.CDSongStyle), COUNT(*), AVG(CAST(s.CDSongNote AS float)) FROM CDSong s JOIN CDHead h ON h.CDHeadId=s.CDHeadId WHERE s.CDSongOrg='T' AND RTRIM(ISNULL(s.CDSongStyle,''))<>'' AND s.CDSongNote BETWEEN 1 AND 5{F("h")} GROUP BY RTRIM(s.CDSongStyle) ORDER BY 2 DESC",
        r => new { style = Str(r[0]), songs = Int(r[1]), avgNote = Math.Round(Dbl(r[2]),2) });

    // 6 — Hörverhalten
    var listening = await Row(
        $"SELECT COUNT(*), SUM(CASE WHEN ISNULL(s.CDSongCount,0)=0 THEN 1 ELSE 0 END), SUM(CAST(ISNULL(s.CDSongCount,0) AS bigint)) FROM CDSong s JOIN CDHead h ON h.CDHeadId=s.CDHeadId WHERE s.CDSongOrg='T'{F("h")}",
        r => new { total = Int(r[0]), unheard = Int(r[1]), plays = r[2] is DBNull ? 0L : Convert.ToInt64(r[2]) });
    var noteVsPlays = await Q(
        $"SELECT s.CDSongNote, AVG(CAST(ISNULL(s.CDSongCount,0) AS float)), COUNT(*) FROM CDSong s JOIN CDHead h ON h.CDHeadId=s.CDHeadId WHERE s.CDSongOrg='T' AND s.CDSongNote BETWEEN 1 AND 5{F("h")} GROUP BY s.CDSongNote ORDER BY s.CDSongNote",
        r => new { note = Int(r[0]), avgPlays = Math.Round(Dbl(r[1]),2), songs = Int(r[2]) });
    var topPlayed = await Q(
        $"SELECT TOP 10 RTRIM(s.CDSongTitel), RTRIM(a.CDArtistSort), s.CDSongCount, s.CDSongNote FROM CDSong s JOIN CDArtist a ON a.CDArtistId=s.CDArtistId JOIN CDHead h ON h.CDHeadId=s.CDHeadId WHERE ISNULL(s.CDSongCount,0)>0{F("h")} ORDER BY s.CDSongCount DESC",
        r => new { title = Str(r[0]), artist = Str(r[1]), plays = Int(r[2]), note = Int(r[3]) });

    // 8 — Datenqualität
    var dataQuality = await Row(
        $@"SELECT
            (SELECT COUNT(*) FROM CDHead h WHERE RTRIM(ISNULL(h.CDHeadGenre,''))=''{F("h")}),
            (SELECT COUNT(*) FROM CDHead h WHERE ISNULL(h.CDHeadJahr,0)=0{F("h")}),
            (SELECT COUNT(*) FROM CDHead h WHERE ISNULL(h.CDHeadCover,0)=0{F("h")}),
            (SELECT COUNT(*) FROM CDHead h WHERE ISNULL(h.CDHeadAMG,0)=0{F("h")}),
            (SELECT COUNT(*) FROM CDHead h WHERE h.CDHeadWertung>5{F("h")}),
            (SELECT COUNT(*) FROM CDSong s JOIN CDHead h ON h.CDHeadId=s.CDHeadId WHERE s.CDSongOrg='T' AND ISNULL(s.CDSongNote,0)=0{F("h")}),
            (SELECT COUNT(*) FROM CDSong s JOIN CDHead h ON h.CDHeadId=s.CDHeadId WHERE s.CDSongCount IS NULL{F("h")})",
        r => new { albumsNoGenre = Int(r[0]), albumsNoYear = Int(r[1]), albumsNoCover = Int(r[2]), albumsNoAmg = Int(r[3]), wertungOutliers = Int(r[4]), songsUnrated = Int(r[5]), songsNullCount = Int(r[6]) });

    var totals = await Row(
        $"SELECT (SELECT COUNT(DISTINCT h.CDArtistId) FROM CDHead h WHERE 1=1{F("h")}), (SELECT MIN(NULLIF(h.CDHeadJahr,0)) FROM CDHead h WHERE h.CDHeadJahr>=1900{F("h")}), (SELECT MAX(h.CDHeadJahr) FROM CDHead h WHERE 1=1{F("h")})",
        r => new { artists = Int(r[0]), yearFrom = Int(r[1]), yearTo = Int(r[2]) });

    return Results.Json(new
    {
        totals,
        facets = new { cats = facetsCat, typs = facetsTyp, genres = facetsGenre },
        noteHist, wertungHist, quality,
        perDecadeAlbums, perDecadeSongs,
        topArtistsByAlbums, topArtistsByNote,
        topGenres, styles,
        listening, noteVsPlays, topPlayed,
        dataQuality
    });
});

// ── GET /stats ────────────────────────────────────────────────────────────────
app.MapGet("/stats", (IWebHostEnvironment env) =>
    Results.File(Path.Combine(env.WebRootPath ?? "wwwroot", "stats.html"), "text/html"));

// ── GET /generator ────────────────────────────────────────────────────────────
app.MapGet("/generator", (IWebHostEnvironment env) =>
    Results.File(Path.Combine(env.WebRootPath ?? "wwwroot", "generator.html"), "text/html"));

// ── GET /remote ──────────────────────────────────────────────────────────────
// Bequemer Pfad für die iPhone-Fernbedienung (sonst auch unter /remote.html).
app.MapGet("/remote", (IWebHostEnvironment env) =>
    Results.File(Path.Combine(env.WebRootPath ?? "wwwroot", "remote.html"), "text/html"));

// ── GET /api/evaluation/album/{id} ────────────────────────────────────────────
app.MapGet("/api/evaluation/album/{id:int}", async (int id) =>
{
    await using var conn = NewConn();
    await conn.OpenAsync();

    double amg = 0, wertung = 0;
    int    cover = 0, wert = 0;
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = @"
            SELECT ISNULL(CDHeadAMG,0), ISNULL(CDHeadCover,0),
                   ISNULL(CDHeadWert,0), ISNULL(CDHeadWertung,0)
            FROM CDHead WHERE CDHeadId = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (await r.ReadAsync())
        {
            amg     = Convert.ToDouble(r[0]);
            cover   = Convert.ToInt32(r[1]);
            wert    = Convert.ToInt32(r[2]);
            wertung = Convert.ToDouble(r[3]);
        }
    }

    var proposals = new List<object>();
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = @"
            SELECT r.CDSongId, r.NoteNeu, ISNULL(s.CDSongNote,0)
            FROM CDSongReRate r
            JOIN CDSong s ON s.CDSongId = r.CDSongId
            WHERE r.CDHeadId = @id AND r.ProcessedAt IS NULL";
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            proposals.Add(new {
                songId  = Convert.ToInt32(r[0]),
                noteNeu = Convert.ToInt32(r[1]),
                noteAlt = Convert.ToInt32(r[2])
            });
    }

    return Results.Json(new { amg, cover, wert, wertung, proposals });
});

// ── POST /api/evaluation/save ─────────────────────────────────────────────────
// Song-Noten → CDSongReRate (Staging für YAMS ReRateForm).
// AMG/Cover/Wert/d1/Wertung → CDHead direkt. CDHeadState = 'V' (Vorgemerkt).
app.MapPost("/api/evaluation/save", async (EvalSaveDto body) =>
{
    if (body.AlbumId <= 0) return Results.BadRequest(new { error = "albumId fehlt" });

    await using var conn = NewConn();
    await conn.OpenAsync();

    int saved = 0;
    foreach (var s in body.Songs ?? new List<EvalSongDto>())
    {
        if (s.Note < 1 || s.Note > 5) continue;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE CDSongReRate
               SET NoteNeu = @n, CreatedAt = SYSDATETIME()
             WHERE CDSongId = @s AND ProcessedAt IS NULL;
            IF @@ROWCOUNT = 0
                INSERT INTO CDSongReRate (CDSongId, CDHeadId, NoteNeu) VALUES (@s, @h, @n);";
        cmd.Parameters.AddWithValue("@s", s.SongId);
        cmd.Parameters.AddWithValue("@h", body.AlbumId);
        cmd.Parameters.AddWithValue("@n", s.Note);
        await cmd.ExecuteNonQueryAsync();
        saved++;
    }

    // d1 aus CDSong + CDSongReRate (zeitgewichtet, CDSongOrg='T')
    double d1 = 0;
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = @"
            SELECT ISNULL(CAST(r.NoteNeu AS float), CAST(NULLIF(s.CDSongNote,0) AS float)) AS EffNote,
                   ISNULL(s.CDSongSecs, 0) AS Secs
            FROM CDSong s
            LEFT JOIN CDSongReRate r ON r.CDSongId = s.CDSongId AND r.ProcessedAt IS NULL
            WHERE s.CDHeadId = @h AND s.CDSongOrg = 'T'";
        cmd.Parameters.AddWithValue("@h", body.AlbumId);
        await using var r = await cmd.ExecuteReaderAsync();
        double sumNote = 0, sumSecs = 0;
        while (await r.ReadAsync())
        {
            if (r.IsDBNull(0)) continue;
            double n = Convert.ToDouble(r[0]);
            double sec = Convert.ToDouble(r[1]);
            sumNote += n * sec;
            sumSecs += sec;
        }
        if (sumSecs > 0) d1 = sumNote / sumSecs;
    }

    double wertung = 0;
    if (d1 > 0 && body.Amg > 0 && body.Cover > 0 && body.Wert > 0)
        wertung = (d1 * 5 + body.Wert * 2 + body.Amg * 2 + body.Cover) / 10;

    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = @"
            UPDATE CDHead
               SET CDHeadAMG     = @amg,
                   CDHeadCover   = @cov,
                   CDHeadWert    = @wert,
                   CDHeadSongs   = @d1,
                   CDHeadWertung = @wertung,
                   CDHeadTotal   = @wertung,
                   CDHeadState   = 'V'
             WHERE CDHeadId = @h";
        cmd.Parameters.AddWithValue("@amg",     body.Amg);
        cmd.Parameters.AddWithValue("@cov",     body.Cover);
        cmd.Parameters.AddWithValue("@wert",    body.Wert);
        cmd.Parameters.AddWithValue("@d1",      d1);
        cmd.Parameters.AddWithValue("@wertung", wertung);
        cmd.Parameters.AddWithValue("@h",       body.AlbumId);
        await cmd.ExecuteNonQueryAsync();
    }

    return Results.Json(new { songsSaved = saved, albumId = body.AlbumId, d1, wertungNew = Math.Round(wertung, 4) });
});

// ── GET /evaluation ───────────────────────────────────────────────────────────
app.MapGet("/evaluation", (IWebHostEnvironment env) =>
    Results.File(Path.Combine(env.WebRootPath ?? "wwwroot", "evaluation.html"), "text/html"));

// ── Server starten ─────────────────────────────────────────────────────────────
app.Run();

// ════════════════════════════════════════════════════════════════════════════════
// DTOs / Relay-Zustand (Fernsteuerung + Neubewertung)
// ════════════════════════════════════════════════════════════════════════════════
record ReRateDto(int Note);
record CommandDto(string? Cmd);
record EvalSongDto(int SongId, int Note);
record EvalSaveDto(int AlbumId, List<EvalSongDto>? Songs, double Amg, int Cover, int Wert);
record NowPlayingDto(int SongId, int HeadId, string? Title, string? Artist, string? Album, int Note, double Pos, double Dur);
record SetFlagDto(string Flag, bool Remove, int? PlaylistId);

// Hält den letzten gemeldeten Player-Zustand und die noch offenen Transport-Kommandos.
class RemoteHub
{
    public NowPlayingDto? NowPlaying;
    public DateTime NowPlayingAt = DateTime.MinValue;
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Commands = new();
}
