using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Volna.Core;

/// <summary>Where everything lives. Nothing is written next to the program, so it can sit in read-only locations.</summary>
public static class AppPaths
{
    public const string AppName = "Volna";

    /// <summary>Overrides the data folder; used by tests and for portable installs.</summary>
    public static string? Override { get; set; } = Environment.GetEnvironmentVariable("VOLNA_DATA");

    public static string Data
    {
        get
        {
            string folder = Override ?? Path.Combine(BaseFolder(), OperatingSystem.IsLinux() ? AppName.ToLowerInvariant() : AppName);
            // It can hold a saved sign-in, so other accounts on the computer must not be able to look inside.
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
            else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return folder;
        }
    }

    static string BaseFolder()
    {
        if (OperatingSystem.IsWindows()) return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support");
        string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return !string.IsNullOrWhiteSpace(xdg) ? xdg : Path.Combine(home, ".local", "share");
    }

    static string Sub(string name)
    {
        string folder = Path.Combine(Data, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static string Tools => Sub("tools");
    public static string Cache => Sub("cache");
    public static string Logs => Sub("logs");
    public static string Settings => Path.Combine(Data, "settings.json");
    public static string Queue => Path.Combine(Data, "queue.json");
    public static string History => Path.Combine(Data, "history.txt");
    public static string Followed => Path.Combine(Data, "followed.json");
    public static string CookieFile => Path.Combine(Data, "youtube-cookies.txt");
    public static string SignInProfile => Path.Combine(Data, "sign-in-profile");

    public static string DefaultDownloads
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string downloads = (OperatingSystem.IsLinux() ? LinuxDownloads(home) : null) ?? Path.Combine(home, "Downloads");
            return Path.Combine(Directory.Exists(downloads) ? downloads : home, AppName);
        }
    }

    // Linux desktops name this folder in the person's language and say so in a file.
    static string? LinuxDownloads(string home)
    {
        try
        {
            string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            string file = Path.Combine(string.IsNullOrWhiteSpace(config) ? Path.Combine(home, ".config") : config, "user-dirs.dirs");
            if (!File.Exists(file)) return null;
            string? line = File.ReadLines(file).FirstOrDefault(l => l.StartsWith("XDG_DOWNLOAD_DIR=", StringComparison.Ordinal));
            return line?[17..].Trim().Trim('"').Replace("$HOME", home, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Writes through a temporary file, so a crash can never leave half a settings or queue file.</summary>
    public static void WriteAtomic(string path, string text)
    {
        string temp = path + ".tmp" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}

/// <summary>
/// JSON as plain trees (Dictionary, List, string, double, bool, null). yt-dlp's output has no fixed shape, and working
/// without reflection keeps the binaries trimmable.
/// </summary>
public static class Json
{
    public static object? Parse(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 512, AllowTrailingCommas = true });
        return Convert(document.RootElement);
    }

    public static Dictionary<string, object?>? ParseObject(string text)
    {
        try { return Parse(text) as Dictionary<string, object?>; }
        catch (JsonException) { return null; }
    }

    static object? Convert(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>();
                foreach (var property in element.EnumerateObject()) map[property.Name] = Convert(property.Value);
                return map;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray()) list.Add(Convert(item));
                return list;
            case JsonValueKind.String: return element.GetString();
            case JsonValueKind.Number: return element.GetDouble();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            default: return null;
        }
    }

    public static string Write(object? value, bool indented = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented, MaxDepth = 512, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteValue(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string text: writer.WriteStringValue(text); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            case double number:
                if (double.IsNaN(number) || double.IsInfinity(number)) writer.WriteNullValue();
                else if (number == Math.Floor(number) && Math.Abs(number) < 9e15) writer.WriteNumberValue((long)number);
                else writer.WriteNumberValue(number);
                break;
            case float number: writer.WriteNumberValue(number); break;
            case int number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case IDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var pair in map) { writer.WritePropertyName(pair.Key); WriteValue(writer, pair.Value); }
                writer.WriteEndObject();
                break;
            case IEnumerable items:
                writer.WriteStartArray();
                foreach (object? item in items) WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            default: writer.WriteStringValue(value.ToString()); break;
        }
    }

    public static string? Str(this IDictionary<string, object?>? map, string key)
    {
        if (map == null || !map.TryGetValue(key, out object? value) || value == null) return null;
        string? text = value as string ?? System.Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static double? Num(this IDictionary<string, object?>? map, string key)
    {
        if (map == null || !map.TryGetValue(key, out object? value)) return null;
        return value switch
        {
            double number => number,
            int number => number,
            long number => number,
            _ => null
        };
    }

    public static bool Flag(this IDictionary<string, object?>? map, string key) => map != null && map.TryGetValue(key, out object? value) && value is true;

    public static object? Raw(this IDictionary<string, object?>? map, string key) => map != null && map.TryGetValue(key, out object? value) ? value : null;

    public static Dictionary<string, object?> Map(params (string Key, object? Value)[] pairs)
    {
        var map = new Dictionary<string, object?>();
        foreach (var (key, value) in pairs) map[key] = value;
        return map;
    }
}

/// <summary>Interface strings. English is the source language; a missing translation falls back to it.</summary>
public static class Loc
{
    static readonly Dictionary<string, Dictionary<string, string>> Tables = new();
    public static string[] Languages { get; } = { "en", "ru" };
    public static string Language { get; private set; } = "en";

    static Dictionary<string, string> Table(string language)
    {
        lock (Tables)
        {
            if (Tables.TryGetValue(language, out var table)) return table;
            table = new Dictionary<string, string>();
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(language + ".json");
            if (stream != null)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                if (Json.Parse(reader.ReadToEnd()) is Dictionary<string, object?> map)
                    foreach (var pair in map) if (pair.Value is string text) table[pair.Key] = text;
            }
            Tables[language] = table;
            return table;
        }
    }

    public static IReadOnlyCollection<string> Keys(string language) => Table(language).Keys;

    public static void Use(string? language)
    {
        if (string.IsNullOrEmpty(language) || language == "auto") language = SystemLanguage();
        Language = Languages.Contains(language) ? language : "en";
    }

    // Without ICU the system's culture reads as invariant, and Linux says it in the environment anyway.
    static string SystemLanguage()
    {
        string culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (culture != "iv") return culture;
        foreach (string name in new[] { "LC_ALL", "LC_MESSAGES", "LANG" })
            if (Environment.GetEnvironmentVariable(name) is { Length: >= 2 } value && value != "C" && value != "POSIX") return value[..2].ToLowerInvariant();
        return "en";
    }

    public static string T(string key)
    {
        if (Table(Language).TryGetValue(key, out string? text)) return text;
        return Table("en").TryGetValue(key, out text) ? text : key;
    }

    public static string T(string key, params object?[] values) => string.Format(CultureInfo.CurrentCulture, T(key), values);
}

public static class Format
{
    // Built by hand: on a system without ICU there are no cultures to ask for.
    static readonly CultureInfo Comma = MakeComma();
    static CultureInfo MakeComma()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = "\u00A0";
        return culture;
    }
    static CultureInfo Culture => Loc.Language == "ru" ? Comma : CultureInfo.InvariantCulture;

    public static string Bytes(double bytes)
    {
        string[] units = { "unit.b", "unit.kb", "unit.mb", "unit.gb", "unit.tb" };
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return bytes.ToString(unit == 0 || bytes >= 100 ? "0" : "0.0", Culture) + " " + Loc.T(units[unit]);
    }

    public static string Speed(double bytesPerSecond) => Loc.T("unit.speed", Bytes(bytesPerSecond));

    public static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return span.TotalHours >= 1
            ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + span.ToString(@"mm\:ss", CultureInfo.InvariantCulture)
            : ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + span.ToString("ss", CultureInfo.InvariantCulture);
    }

    public static string Views(double views)
    {
        if (views >= 1e9) return Loc.T("views.billion", (views / 1e9).ToString(views >= 1e10 ? "0" : "0.#", Culture));
        if (views >= 1e6) return Loc.T("views.million", (views / 1e6).ToString(views >= 1e7 ? "0" : "0.#", Culture));
        if (views >= 1e3) return Loc.T("views.thousand", (views / 1e3).ToString(views >= 1e4 ? "0" : "0.#", Culture));
        long n = (long)views;
        if (Loc.Language != "ru") return Loc.T(n == 1 ? "views.one" : "views.many", n);
        string key = n % 10 == 1 && n % 100 != 11 ? "views.one" : n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14) ? "views.few" : "views.many";
        return Loc.T(key, n);
    }

    public static string QualityBadge(int height)
    {
        if (height >= 4320) return "8K";
        if (height >= 2160) return "4K";
        if (height >= 1440) return "2K";
        return height <= 0 ? "" : height.ToString(CultureInfo.InvariantCulture) + "p";
    }
}
