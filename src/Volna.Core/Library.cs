using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Volna.Core;

/// <summary>One song from another service's export.</summary>
public sealed record LibraryTrack(string Artist, string Title, string Album = "", double? Seconds = null, string List = "", string? Link = null, string? Isrc = null)
{
    public string Entry => Link ?? Engine.SearchEntry(Artist.Length > 0 ? Artist + " - " + Title : Title);
}

/// <summary>
/// Reads the files people get when they leave a music service: a CSV from Exportify, TuneMyMusic, Soundiiz or Google
/// Takeout, Spotify's own account export (JSON), an Apple Music library (XML), or a playlist (M3U). No account or service
/// is involved, only the file.
/// </summary>
public static class LibraryImport
{
    public const long MaxFileBytes = 50_000_000;
    static readonly string[] Extensions = { ".csv", ".tsv", ".json", ".xml", ".m3u", ".m3u8" };
    static readonly char[] Delimiters = { ',', ';', '\t', '|' };
    static readonly char[] PathSeparators = { '/', '\\' };

    static readonly string[] TitleColumns = { "trackname", "tracktitle", "track", "songname", "songtitle", "song", "title", "name", "название", "названиетрека", "названиепесни", "трек", "песня" };
    static readonly string[] ArtistColumns = { "artistnames", "artistname", "artistname1", "artist", "artists", "band", "albumartistname", "исполнитель", "исполнители", "артист", "артисты", "группа" };
    static readonly string[] AlbumColumns = { "albumname", "albumtitle", "album", "альбом" };
    static readonly string[] DurationColumns = { "trackdurationms", "durationms", "tracklengthms", "lengthms", "duration", "durationseconds", "length", "time", "длительность", "длина", "время" };
    static readonly string[] IsrcColumns = { "isrc" };
    static readonly string[] ListColumns = { "playlistname", "playlist", "плейлист" };
    static readonly string[] VideoColumns = { "videoid" };

    public static bool Handles(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || Directory.Exists(path);

    /// <summary>A file, a zip (exports usually come as one) or a folder of them.</summary>
    public static List<LibraryTrack> ReadFile(string path)
    {
        var tracks = new List<LibraryTrack>();
        if (Directory.Exists(path))
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                if (Handles(file) && new FileInfo(file).Length <= MaxFileBytes) tracks.AddRange(ReadFile(file));
            return tracks;
        }
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return ParseNamed(ReadText(File.ReadAllBytes(path)), path);
        using ZipArchive zip = ZipFile.OpenRead(path);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.Length > MaxFileBytes || !Extensions.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase)) continue;
            using Stream stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var found = ParseNamed(ReadText(memory.ToArray()), entry.Name);
            // An account's export also lists every video commented on or uploaded. Only playlists and music are the library.
            if (found.TrueForAll(t => t.Link != null && t.Title.Length == 0) && !LibraryFolder(entry.FullName)) continue;
            tracks.AddRange(found);
        }
        return tracks;
    }

    static readonly string[] LibraryWords = { "playlist", "плейлист", "music", "музык" };

    static bool LibraryFolder(string path) => LibraryWords.Any(word => path.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Old tools and spreadsheets save in the system's code page rather than UTF-8. What is not valid UTF-8 is read as
    /// Windows-1251 when most of its letters fit there, and as Shift-JIS otherwise.
    /// </summary>
    public static string ReadText(byte[] data)
    {
        if (data.Length >= 2 && ((data[0] == 0xFF && data[1] == 0xFE) || (data[0] == 0xFE && data[1] == 0xFF)))
            return (data[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(data).TrimStart('\uFEFF');
        try { return new UTF8Encoding(false, true).GetString(data).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int high = data.Count(b => b >= 0x80), cyrillic = data.Count(b => b >= 0xC0);
        return Encoding.GetEncoding(cyrillic * 10 >= high * 7 ? 1251 : 932).GetString(data);
    }

    public static bool IsTableFile(string path) => path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase);

    static List<LibraryTrack> ParseNamed(string text, string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        return Path.GetExtension(file).ToUpperInvariant() is ".M3U" or ".M3U8" ? FromM3u(text, name) : Parse(text, name);
    }

    /// <summary>Empty when the text is none of these, such as an ordinary list of song names.</summary>
    public static List<LibraryTrack> Parse(string? text, string listName = "")
    {
        string body = (text ?? "").TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (body.Length == 0) return new();
        try
        {
            if (body[0] is '{' or '[') return FromJson(body, listName);
            if (body[0] == '<') return FromApple(body, listName);
        }
        catch (Exception ex) when (ex is JsonException or XmlException) { return new(); }
        return body.StartsWith("#EXT", StringComparison.Ordinal) ? FromM3u(body, listName) : FromTable(body, listName);
    }

    // ---------- CSV ----------

    static List<LibraryTrack> FromTable(string body, string listName)
    {
        int firstBreak = body.AsSpan().IndexOfAny('\r', '\n');
        string firstLine = firstBreak < 0 ? body : body[..firstBreak];
        char delimiter = Delimiters.OrderByDescending(d => firstLine.Count(c => c == d)).First();
        if (!firstLine.Contains(delimiter)) return new();
        List<List<string>> rows = Rows(body, delimiter);
        if (rows.Count < 2) return new();
        List<string> header = rows[0].Select(Column).ToList();
        int Find(string[] names) { foreach (string name in names) { int at = header.IndexOf(name); if (at >= 0) return at; } return -1; }
        int title = Find(TitleColumns), artist = Find(ArtistColumns), album = Find(AlbumColumns), duration = Find(DurationColumns), list = Find(ListColumns), video = Find(VideoColumns), isrc = Find(IsrcColumns);
        // Exportify translates its headers into the browser's language, but they always come in the same order.
        bool translated = (title < 0 || artist < 0) && header.Count >= 17 && rows[1].Count >= 17 && rows[1][0].StartsWith("spotify:track:", StringComparison.Ordinal);
        if (translated) (title, artist, album, duration, isrc) = (1, 3, 5, 12, 16);
        if (title < 0 && video < 0) return new();
        bool milliseconds = translated || (duration >= 0 && header[duration].EndsWith("ms", StringComparison.Ordinal));
        string Cell(List<string> row, int at) => at >= 0 && at < row.Count ? row[at].Trim() : "";

        var tracks = new List<LibraryTrack>();
        foreach (var row in rows.Skip(1))
        {
            string name = Cell(row, title), id = Cell(row, video);
            string? link = id.Length > 0 ? "https://music.youtube.com/watch?v=" + Uri.EscapeDataString(id) : null;
            if (name.Length == 0 && link == null) continue;
            string owner = Cell(row, list);
            tracks.Add(new LibraryTrack(Artists(Cell(row, artist)), name, Cell(row, album), Seconds(Cell(row, duration), milliseconds), owner.Length > 0 ? owner : listName, link, Cell(row, isrc) is { Length: > 0 } code ? code : null));
        }
        return tracks;
    }

    static string Column(string name) => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // Several artists come separated by ";" from some tools. A comma stays: "Tyler, The Creator" is one artist, which
    // Exportify writes with a backslash.
    static string Artists(string text) => string.Join(", ", text.Replace("\\,", ",", StringComparison.Ordinal).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    static double? Seconds(string text, bool milliseconds)
    {
        if (Engine.ParseTime(text.TrimEnd('s')) is not { } value || value <= 0) return null;
        return milliseconds || (!text.Contains(':', StringComparison.Ordinal) && value >= 10000) ? value / 1000 : value;
    }

    // Quotes may hold delimiters and line breaks, and "" is a quote.
    static List<List<string>> Rows(string body, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        void EndCell() { row.Add(cell.ToString()); cell.Clear(); }
        void EndRow() { EndCell(); if (row.Exists(c => c.Length > 0)) rows.Add(row); row = new(); }
        for (int n = 0; n < body.Length; n++)
        {
            char c = body[n];
            if (quoted)
            {
                if (c != '"') cell.Append(c);
                else if (n + 1 < body.Length && body[n + 1] == '"') { cell.Append('"'); n++; }
                else quoted = false;
            }
            else if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == delimiter) EndCell();
            else if (c is '\n') EndRow();
            else if (c != '\r') cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) EndRow();
        return rows;
    }

    /// <summary>
    /// A table whose columns have no names this program knows: from a script, a spreadsheet, any site. A person confirms
    /// what the columns are, so no service needs its own support here. Null when the text is not a table at all.
    /// </summary>
    public static ImportTable? GuessTable(string? text, string listName = "")
    {
        string body = (text ?? "").TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (body.Length == 0 || body[0] is '{' or '[' or '<' or '#') return null;
        int firstBreak = body.AsSpan().IndexOfAny('\r', '\n');
        string firstLine = firstBreak < 0 ? body : body[..firstBreak];
        foreach (char delimiter in Delimiters.Where(d => firstLine.Contains(d)).OrderByDescending(d => firstLine.Count(c => c == d)))
        {
            List<List<string>> rows = Rows(body, delimiter);
            if (rows.Count < 2) continue;
            var shape = rows.GroupBy(r => r.Count).OrderByDescending(g => g.Count()).First();
            // A comma here and there in a list of songs does not make it a table.
            if (shape.Key < 2 || shape.Count() < rows.Count * 0.9) continue;
            var usable = rows.Where(r => r.Count == shape.Key).ToList();
            if (usable.All(r => r.Exists(c => c.Contains("://", StringComparison.Ordinal)))) return null; // a list of links has its own way in
            return new ImportTable(usable, shape.Key, body, listName);
        }
        return null;
    }

    // ---------- Spotify's account export ----------

    static List<LibraryTrack> FromJson(string body, string listName)
    {
        var tracks = new List<LibraryTrack>();
        Walk(Json.Parse(body), listName, tracks);
        return tracks;
    }

    static void Walk(object? node, string list, List<LibraryTrack> found)
    {
        if (node is List<object?> items)
        {
            foreach (object? value in items) Walk(value, list, found);
            return;
        }
        if (node is not Dictionary<string, object?> map) return;
        // Plays are not the library, and however Spotify's export words it, nobody wants what they hid.
        if (map.Raw("msPlayed") != null || map.Raw("ms_played") != null) return;
        string? title = map.Str("trackName") ?? map.Str("track") ?? map.Str("title");
        string? artist = map.Str("artistName") ?? map.Str("artist");
        if (title != null && artist != null)
        {
            double? seconds = map.Num("durationMs") is { } ms ? ms / 1000 : map.Num("duration");
            found.Add(new LibraryTrack(artist, title, map.Str("albumName") ?? map.Str("album") ?? "", seconds, list));
            return;
        }
        // A playlist in the export names the list its songs belong to.
        string inner = map.Str("name") is { } name && map.Raw("items") != null ? name : list;
        foreach (var (key, value) in map)
            if (!key.StartsWith("banned", StringComparison.OrdinalIgnoreCase)) Walk(value, inner, found);
    }

    // ---------- Apple Music ----------

    static List<LibraryTrack> FromApple(string body, string listName)
    {
        // Its files declare a DTD, which is skipped rather than fetched.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var text = new StringReader(body);
        using var reader = XmlReader.Create(text, settings);
        XElement? root = XDocument.Load(reader).Root?.Element("dict");
        var tracks = new List<LibraryTrack>();
        if (root == null || Fields(root) is not { } top || top.GetValueOrDefault("Tracks") is not { } library) return tracks;
        var byId = new Dictionary<string, LibraryTrack>(StringComparer.Ordinal);
        foreach (var (id, song) in Fields(library))
        {
            var fields = Fields(song);
            string? name = fields.GetValueOrDefault("Name")?.Value;
            if (string.IsNullOrWhiteSpace(name) || OtherMedia.Any(fields.ContainsKey)) continue;
            double? seconds = double.TryParse(fields.GetValueOrDefault("Total Time")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) ? ms / 1000 : null;
            byId[id] = new LibraryTrack(fields.GetValueOrDefault("Artist")?.Value ?? "", name, fields.GetValueOrDefault("Album")?.Value ?? "", seconds, listName);
            tracks.Add(byId[id]);
        }
        // Lists the person made. The built-in ones and smart lists only repeat the library.
        foreach (XElement list in top.GetValueOrDefault("Playlists")?.Elements("dict") ?? Enumerable.Empty<XElement>())
        {
            var fields = Fields(list);
            if (fields.GetValueOrDefault("Name")?.Value is not { Length: > 0 } title || BuiltInList.Any(fields.ContainsKey)) continue;
            foreach (XElement entry in fields.GetValueOrDefault("Playlist Items")?.Elements("dict") ?? Enumerable.Empty<XElement>())
                if (Fields(entry).GetValueOrDefault("Track ID")?.Value is { } id && byId.TryGetValue(id, out var track)) tracks.Add(track with { List = title });
        }
        return tracks;
    }

    static readonly string[] OtherMedia = { "Podcast", "Movie", "TV Show" };
    static readonly string[] BuiltInList = { "Master", "Distinguished Kind", "Smart Info", "Folder" };

    // A property list keeps each key and its value as neighbouring elements.
    static Dictionary<string, XElement> Fields(XElement dict)
    {
        var fields = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (XElement key in dict.Elements("key"))
            if (key.ElementsAfterSelf().FirstOrDefault() is { } value) fields[key.Value] = value;
        return fields;
    }

    // ---------- Playlists ----------

    static List<LibraryTrack> FromM3u(string body, string listName)
    {
        var tracks = new List<LibraryTrack>();
        string? info = null;
        double? seconds = null;
        foreach (string raw in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string line = raw.TrimStart('﻿');
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = line.IndexOf(',', StringComparison.Ordinal);
                info = comma < 0 ? null : line[(comma + 1)..].Trim();
                seconds = comma < 0 ? null : Engine.ParseTime(line[8..comma]) is { } value and > 0 ? value : null;
                continue;
            }
            if (line.StartsWith('#')) continue;
            bool web = line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (web && !Uri.TryCreate(line, UriKind.Absolute, out _)) { info = null; seconds = null; continue; }
            string location = line.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? Uri.UnescapeDataString(line) : line;
            string name = info ?? (web ? "" : Path.GetFileNameWithoutExtension(location[(location.LastIndexOfAny(PathSeparators) + 1)..]));
            int separator = name.IndexOf(" - ", StringComparison.Ordinal);
            string artist = separator > 0 ? name[..separator].Trim() : "", title = separator > 0 ? name[(separator + 3)..].Trim() : name;
            if (web || title.Length > 0) tracks.Add(new LibraryTrack(artist, title, "", seconds, listName, web ? line : null));
            info = null;
            seconds = null;
        }
        return tracks;
    }
}

/// <summary>A table being imported, with a guess at its columns that the person can correct.</summary>
public sealed class ImportTable
{
    readonly List<List<string>> rows;

    public ImportTable(List<List<string>> rows, int columns, string text, string listName)
    {
        this.rows = rows;
        Columns = columns;
        Text = text;
        ListName = listName;
        Guess();
    }

    public int Columns { get; }
    public string Text { get; }
    public string ListName { get; }
    public bool FirstRowIsHeader { get; set; }
    public int TitleColumn { get; set; }
    public int ArtistColumn { get; set; } = -1; // -1: no such column
    public int DurationColumn { get; private set; } = -1;

    /// <summary>What a column holds, for choosing it in a list.</summary>
    public string Sample(int column)
    {
        string value = rows[0][column].Trim();
        return value.Length > 40 ? value[..40] + "…" : value;
    }

    public List<LibraryTrack> Tracks()
    {
        var tracks = new List<LibraryTrack>();
        foreach (var row in rows.Skip(FirstRowIsHeader ? 1 : 0))
        {
            string title = row[TitleColumn].Trim();
            if (title.Length == 0) continue;
            string artist = ArtistColumn >= 0 ? row[ArtistColumn].Trim() : "";
            double? seconds = DurationColumn >= 0 ? Engine.ParseTime(row[DurationColumn]) : null;
            tracks.Add(new LibraryTrack(artist, title, "", seconds is > 0 ? seconds : null, ListName));
        }
        return tracks;
    }

    static bool IsTime(string cell) => cell.Contains(':', StringComparison.Ordinal) && Engine.ParseTime(cell) != null;

    // Names repeat in a library's artist column and rarely in its title column, whatever order they come in.
    void Guess()
    {
        var body = rows.Skip(1).Take(5000).ToList();
        var text = new List<(int Column, double Variety)>();
        for (int n = 0; n < Columns; n++)
        {
            var cells = body.Select(r => r[n].Trim()).ToList();
            int filled = cells.Count(c => c.Length > 0);
            if (filled == 0) continue;
            if (cells.Count(IsTime) * 2 > filled)
            {
                DurationColumn = n;
                FirstRowIsHeader = !IsTime(rows[0][n]); // a time under a word: that word names the column
                continue;
            }
            if (cells.Count(c => c.Any(char.IsLetter)) * 2 < filled || cells.Any(c => c.Contains("://", StringComparison.Ordinal))) continue;
            text.Add((n, cells.Distinct(StringComparer.OrdinalIgnoreCase).Count() / (double)filled));
        }
        if (text.Count == 0) { TitleColumn = 0; return; }
        // With nothing to tell them apart, artist comes first, as people write it.
        var title = text.OrderByDescending(c => c.Variety).ThenByDescending(c => c.Column).First();
        TitleColumn = title.Column;
        var others = text.Where(c => c.Column != title.Column).ToList();
        if (others.Count > 0) ArtistColumn = others.OrderBy(c => c.Variety).ThenBy(c => c.Column).First().Column;
    }
}

/// <summary>What has been downloaded, so a file that was moved or deleted can still count as done.</summary>
public static class DownloadHistory
{
    static readonly object Gate = new();
    static HashSet<string>? known;
    static string? loadedFrom;

    static HashSet<string> Known()
    {
        string path = AppPaths.History;
        if (known != null && loadedFrom == path) return known;
        known = new HashSet<string>(StringComparer.Ordinal);
        loadedFrom = path;
        try { if (File.Exists(path)) known.UnionWith(File.ReadLines(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return known;
    }

    public static bool Contains(string key)
    {
        lock (Gate) return Known().Contains(key);
    }

    public static void Add(IEnumerable<string> keys)
    {
        lock (Gate)
        {
            var fresh = keys.Where(Known().Add).ToList();
            if (fresh.Count == 0) return;
            try { File.AppendAllLines(AppPaths.History, fresh); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>A playlist whose new videos are fetched by themselves.</summary>
public sealed class FollowedList
{
    /// <summary>What it held when followed or last looked at: only what comes after that is new.</summary>
    public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
    public bool Audio { get; set; } = true;
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime Checked { get; set; }

    public static List<FollowedList> Load()
    {
        var lists = new List<FollowedList>();
        try
        {
            if (!File.Exists(AppPaths.Followed) || Json.Parse(File.ReadAllText(AppPaths.Followed)) is not List<object?> saved) return lists;
            foreach (var entry in saved.OfType<Dictionary<string, object?>>())
            {
                if (entry.Str("url") is not { } url) continue;
                DateTime.TryParse(entry.Str("checked"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime when);
                var list = new FollowedList { Url = url, Name = entry.Str("name") ?? url, Checked = when, Audio = entry.Raw("audio") is not false };
                if (entry.Raw("seen") is List<object?> seen) list.Seen.UnionWith(seen.OfType<string>());
                lists.Add(list);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return lists;
    }

    public static void Save(IEnumerable<FollowedList> lists)
    {
        try { AppPaths.WriteAtomic(AppPaths.Followed, Json.Write(lists.Select(l => Json.Map(("url", l.Url), ("name", l.Name), ("audio", l.Audio), ("seen", l.Seen.ToList()), ("checked", l.Checked.ToString("O", CultureInfo.InvariantCulture)))).ToList(), true)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>An .m3u8 file that a player can open as the list. Songs already in it stay, so a later run adds to it.</summary>
public static class PlaylistFile
{
    public sealed record Song(string File, string Title, double? Seconds);

    public static string Write(string folder, string name, IEnumerable<Song> songs)
    {
        string path = Path.Combine(folder, Engine.SafeFileName(name) + ".m3u8");
        var lines = new List<string> { "#EXTM3U" };
        var present = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            string[] old = File.ReadAllLines(path);
            for (int n = 0; n < old.Length; n++)
            {
                if (old[n].Length == 0 || old[n].StartsWith('#')) continue;
                if (!File.Exists(Path.Combine(folder, old[n])) || !present.Add(old[n])) continue;
                if (n > 0 && old[n - 1].StartsWith("#EXTINF", StringComparison.Ordinal)) lines.Add(old[n - 1]);
                lines.Add(old[n]);
            }
        }
        foreach (Song song in songs)
        {
            string file = Path.GetRelativePath(folder, song.File).Replace('\\', '/');
            if (!present.Add(file)) continue;
            lines.Add("#EXTINF:" + ((int)(song.Seconds ?? -1)).ToString(CultureInfo.InvariantCulture) + "," + song.Title);
            lines.Add(file);
        }
        AppPaths.WriteAtomic(path, string.Join("\n", lines) + "\n");
        return path;
    }
}
