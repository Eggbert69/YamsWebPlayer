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
               s.CDSongSecs,
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
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        UPDATE CDSong
        SET    CDSongCount    = ISNULL(CDSongCount,    0) + 1,
               CDSongWebCount = ISNULL(CDSongWebCount, 0) + 1
        WHERE  CDSongId = @Id";
    cmd.Parameters.AddWithValue("@Id", songId);
    var rows = await cmd.ExecuteNonQueryAsync();
    return rows > 0 ? Results.Ok() : Results.NotFound();
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

// ── Server starten ─────────────────────────────────────────────────────────────
app.Run();
