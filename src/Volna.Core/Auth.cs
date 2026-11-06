using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;

namespace Volna.Core;

public enum SignInStatus { SignedIn, NotSignedIn, CannotRead }

public sealed record SignInCheck(SignInStatus Status, string Detail = "");

/// <summary>A browser yt-dlp can read cookies from.</summary>
public sealed record BrowserInfo(string Id, string Name, bool IsDefault);

/// <summary>
/// How Volna acts as the person on the sites they use. The normal way: it reads the sign-ins of the browser they
/// already use, live on each request, so nothing is copied, nothing goes stale, and it works for every site they are
/// signed in to. Where a browser refuses that (Chrome on Windows encrypts its cookies against other programs), a separate
/// sign-in window for YouTube or an imported cookies.txt takes over.
/// Passwords never pass through this program.
/// </summary>
public static class Auth
{
    static string StateFile => Path.Combine(AppPaths.Data, "auth.json");

    public static string Mode { get; private set; } = "none";       // none, browser, file
    public static string? Browser { get; private set; }             // yt-dlp browser name in browser mode
    public static string? Profile { get; private set; }             // a profile folder, for the separate sign-in window

    static Auth() => Load();

    public static bool SignedIn => Mode != "none";

    /// <summary>Set when the browser cannot be read. Until it is chosen again, work goes on without a sign-in.</summary>
    public static bool Suspended { get; private set; }

    /// <summary>True the first time, so that it is said once.</summary>
    public static bool Suspend()
    {
        if (Mode != "browser" || Suspended) return false;
        Suspended = true;
        return true;
    }

    public static string Describe() => Mode switch
    {
        "browser" => Loc.T("auth.via", Browsers.DisplayName(Browser ?? "")),
        "file" => Loc.T("auth.viafile"),
        _ => Loc.T("auth.none")
    };

    public static void Load()
    {
        Suspended = false;
        Mode = "none"; Browser = null; Profile = null;
        try
        {
            if (!File.Exists(StateFile)) return;
            var state = Json.ParseObject(File.ReadAllText(StateFile));
            string? mode = state.Str("mode");
            if (mode == "browser" && state.Str("browser") != null) { Mode = mode; Browser = state.Str("browser"); Profile = state.Str("profile"); }
            else if (mode == "file" && File.Exists(AppPaths.CookieFile)) Mode = mode;
        }
        catch (IOException) { }
    }

    static void Save() => AppPaths.WriteAtomic(StateFile, Json.Write(Json.Map(("mode", Mode), ("browser", Browser), ("profile", Profile)), true));

    public static void UseBrowser(string browser, string? profile = null) { Suspended = false; Mode = "browser"; Browser = browser; Profile = profile; Save(); }

    public static void UseCookieFile() { Mode = "file"; Browser = null; Profile = null; Save(); }

    /// <summary>Forgets the sign-in here. It also removes the separate profile and any saved cookies; the browser itself is untouched.</summary>
    public static void SignOut()
    {
        Suspended = false;
        Mode = "none"; Browser = null; Profile = null;
        Save();
        try { File.Delete(AppPaths.CookieFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Task.Run(() =>
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (Directory.Exists(AppPaths.SignInProfile)) Directory.Delete(AppPaths.SignInProfile, true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(1000); }
            }
        });
    }

    /// <summary>
    /// Arguments for one yt-dlp run. Cookie files are rewritten by yt-dlp when YouTube rotates cookies, so parallel
    /// processes each get their own copy, and the copy from a clean run replaces the saved file.
    /// </summary>
    public static AuthLease Lease()
    {
        if (Mode == "browser" && !Suspended)
            return new AuthLease(new[] { "--cookies-from-browser", Browser + (Profile != null ? ":" + Profile : "") }, null, default);
        if (Mode == "file" && File.Exists(AppPaths.CookieFile))
        {
            try
            {
                string copy = Path.Combine(AppPaths.Cache, "cookies-" + Guid.NewGuid().ToString("N") + ".txt");
                File.Copy(AppPaths.CookieFile, copy);
                return new AuthLease(new[] { "--cookies", copy }, copy, File.GetLastWriteTimeUtc(AppPaths.CookieFile));
            }
            catch (IOException) { return new AuthLease(new[] { "--cookies", AppPaths.CookieFile }, null, default); }
        }
        return new AuthLease(Array.Empty<string>(), null, default);
    }

    public static void CleanTemporaryCopies()
    {
        // Only old ones: another Volna process may be using a recent copy.
        try { foreach (string file in Directory.GetFiles(AppPaths.Cache, "cookies-*.txt")) if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1)) File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Reads a browser's cookies: can they be read, and is it signed in to YouTube? Nothing is kept.</summary>
    public static async Task<SignInCheck> CheckBrowserAsync(string browser, string? profile = null, CancellationToken cancel = default)
    {
        string export = Path.Combine(AppPaths.Cache, "cookies-check-" + Guid.NewGuid().ToString("N") + ".txt");
        var output = new StringBuilder();
        using var runner = new ProcessRunner();
        using var stop = cancel.Register(runner.Cancel);
        try
        {
            // Without a link yt-dlp reports a usage error, but only after it has loaded and written the cookies.
            await runner.RunAsync(ToolManager.YtDlp, new[] { "--ignore-config", "--no-plugin-dirs", "--cookies-from-browser", browser + (profile != null ? ":" + profile : ""), "--cookies", export },
                line => { lock (output) output.AppendLine(line); }).ConfigureAwait(false);
            string log = output.ToString();
            string lower = log.ToLowerInvariant();
            if (lower.Contains("could not copy") || lower.Contains("failed to decrypt") || lower.Contains("cannot decrypt") || lower.Contains("dpapi")
                || lower.Contains("could not find") || lower.Contains("permission denied") || lower.Contains("operation not permitted") || lower.Contains("unsupported browser"))
                return new SignInCheck(SignInStatus.CannotRead, log.Trim());
            if (!File.Exists(export)) return new SignInCheck(SignInStatus.CannotRead, log.Trim());
            return File.ReadLines(export).Any(IsLoginLine) ? new SignInCheck(SignInStatus.SignedIn) : new SignInCheck(SignInStatus.NotSignedIn);
        }
        finally { try { File.Delete(export); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    static bool IsYouTubeLine(string line)
    {
        string[] fields = line.Split('\t');
        if (fields.Length < 7) return false;
        string domain = fields[0].StartsWith("#HttpOnly_", StringComparison.Ordinal) ? fields[0][10..] : fields[0];
        if (domain.StartsWith('#')) return false;
        return domain == "youtube.com" || domain.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLoginLine(string line) => IsYouTubeLine(line) && line.Split('\t')[5] == "LOGIN_INFO";

    public static bool IsCookieLine(string line) => line.Split('\t').Length >= 7 && (!line.StartsWith('#') || line.StartsWith("#HttpOnly_", StringComparison.Ordinal));

    /// <summary>Takes a Netscape cookies.txt made elsewhere, for whichever sites it covers.</summary>
    public static bool ImportCookieFile(string path)
    {
        var lines = File.ReadLines(path).Where(IsCookieLine).ToList();
        if (lines.Count == 0) return false;
        WriteCookieFile(lines);
        UseCookieFile();
        return true;
    }

    public static void WriteCookieFile(IEnumerable<string> lines, string? target = null)
    {
        AppPaths.WriteAtomic(target ?? AppPaths.CookieFile, "# Netscape HTTP Cookie File\n# Volna: your sign-in. Do not share this file.\n\n" + string.Join("\n", lines) + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target ?? AppPaths.CookieFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

/// <summary>What one yt-dlp run should use for sign-in. Complete() must be called when it ends.</summary>
public sealed class AuthLease
{
    readonly string? copy;
    public IReadOnlyList<string> Args { get; }

    readonly DateTime savedAt;

    internal AuthLease(IReadOnlyList<string> args, string? copy, DateTime savedAt) { Args = args; this.copy = copy; this.savedAt = savedAt; }

    public void Complete(bool cleanRun)
    {
        if (copy == null) return;
        try
        {
            // Only a clean run may replace the sign-in, never after the person signed out or signed in anew, and never
            // with a copy that lost it. If another run saved first, its newer cookies stay.
            bool hadLogin = File.ReadLines(AppPaths.CookieFile).Any(Auth.IsLoginLine);
            if (cleanRun && Auth.Mode == "file" && File.GetLastWriteTimeUtc(AppPaths.CookieFile) == savedAt && File.ReadLines(copy).Any(hadLogin ? Auth.IsLoginLine : Auth.IsCookieLine))
                File.Copy(copy, AppPaths.CookieFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { File.Delete(copy); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public static class Browsers
{
    static readonly (string Id, string Name)[] Known = {
        ("firefox", "Firefox"), ("chrome", "Google Chrome"), ("edge", "Microsoft Edge"), ("brave", "Brave"),
        ("opera", "Opera"), ("vivaldi", "Vivaldi"), ("chromium", "Chromium"), ("safari", "Safari"), ("whale", "Naver Whale") };

    public static string DisplayName(string id) => Known.FirstOrDefault(b => b.Id == id).Name ?? id;

    public static bool IsChromium(string id) => id is "chrome" or "edge" or "brave" or "opera" or "vivaldi" or "chromium" or "whale";

    /// <summary>Browsers present on this computer, the default one first.</summary>
    public static List<BrowserInfo> Installed()
    {
        string? fallback = DefaultBrowser();
        var result = new List<BrowserInfo>();
        foreach (var (id, name) in Known)
            if (id == fallback || Folders(id).Any(Directory.Exists)) result.Add(new BrowserInfo(id, name, id == fallback));
        return result.OrderByDescending(b => b.IsDefault).ToList();
    }

    static string[] Folders(string id)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return id switch
            {
                "firefox" => new[] { Path.Combine(roaming, "Mozilla", "Firefox") },
                "chrome" => new[] { Path.Combine(local, "Google", "Chrome", "User Data") },
                "edge" => new[] { Path.Combine(local, "Microsoft", "Edge", "User Data") },
                "brave" => new[] { Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data") },
                "opera" => new[] { Path.Combine(roaming, "Opera Software", "Opera Stable") },
                "vivaldi" => new[] { Path.Combine(local, "Vivaldi", "User Data") },
                "chromium" => new[] { Path.Combine(local, "Chromium", "User Data") },
                "whale" => new[] { Path.Combine(local, "Naver", "Naver Whale", "User Data") },
                _ => Array.Empty<string>()
            };
        }
        if (OperatingSystem.IsMacOS())
        {
            string support = Path.Combine(home, "Library", "Application Support");
            return id switch
            {
                "firefox" => new[] { Path.Combine(support, "Firefox") },
                "chrome" => new[] { Path.Combine(support, "Google", "Chrome") },
                "edge" => new[] { Path.Combine(support, "Microsoft Edge") },
                "brave" => new[] { Path.Combine(support, "BraveSoftware", "Brave-Browser") },
                "opera" => new[] { Path.Combine(support, "com.operasoftware.Opera") },
                "vivaldi" => new[] { Path.Combine(support, "Vivaldi") },
                "chromium" => new[] { Path.Combine(support, "Chromium") },
                "safari" => new[] { Path.Combine(home, "Library", "Safari") },
                "whale" => new[] { Path.Combine(support, "Naver", "Whale") },
                _ => Array.Empty<string>()
            };
        }
        string config = Path.Combine(home, ".config");
        return id switch
        {
            "firefox" => new[] { Path.Combine(home, ".mozilla", "firefox"), Path.Combine(home, "snap", "firefox", "common", ".mozilla", "firefox"), Path.Combine(home, ".var", "app", "org.mozilla.firefox", ".mozilla", "firefox") },
            "chrome" => new[] { Path.Combine(config, "google-chrome") },
            "edge" => new[] { Path.Combine(config, "microsoft-edge") },
            "brave" => new[] { Path.Combine(config, "BraveSoftware", "Brave-Browser") },
            "opera" => new[] { Path.Combine(config, "opera") },
            "vivaldi" => new[] { Path.Combine(config, "vivaldi") },
            "chromium" => new[] { Path.Combine(config, "chromium") },
            "whale" => new[] { Path.Combine(config, "naver-whale") },
            _ => Array.Empty<string>()
        };
    }

    /// <summary>The system's default browser as a yt-dlp browser name; null when it cannot be told or is not supported.</summary>
    public static string? DefaultBrowser()
    {
        try
        {
            string? handler = null;
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
                handler = key?.GetValue("ProgId") as string;
            }
            else if (OperatingSystem.IsMacOS())
            {
                string plist = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Preferences", "com.apple.LaunchServices", "com.apple.launchservices.secure.plist");
                var map = File.Exists(plist) ? Json.ParseObject(Capture("plutil", "-convert", "json", "-o", "-", plist)) : null;
                foreach (var entry in (map.Raw("LSHandlers") as List<object?> ?? new()).OfType<Dictionary<string, object?>>())
                    if (entry.Str("LSHandlerURLScheme") == "https") handler = entry.Str("LSHandlerRoleAll");
                if (handler == null) return "safari"; // the system default when none was ever chosen
            }
            else handler = Capture("xdg-settings", "get", "default-web-browser");
            return FromHandler(handler);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Maps a Windows ProgId, macOS bundle id or Linux desktop file to a browser name. Public for tests.</summary>
    public static string? FromHandler(string? handler)
    {
        string text = (handler ?? "").ToLowerInvariant();
        if (text.Contains("firefox") || text.Contains("mozilla")) return "firefox";
        if (text.Contains("msedge") || text.Contains("edge")) return "edge";
        if (text.Contains("brave")) return "brave";
        if (text.Contains("vivaldi")) return "vivaldi";
        if (text.Contains("opera")) return "opera";
        if (text.Contains("whale")) return "whale";
        if (text.Contains("chromium")) return "chromium";
        if (text.Contains("chrome")) return "chrome";
        if (text.Contains("safari")) return "safari";
        return null;
    }

    static string Capture(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException();
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    /// <summary>The program file of a browser, for the separate sign-in window.</summary>
    public static string? Executable(string id)
    {
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), programs86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates;
        if (OperatingSystem.IsWindows())
        {
            string[] relative = id switch
            {
                "firefox" => new[] { @"Mozilla Firefox\firefox.exe" },
                "chrome" => new[] { @"Google\Chrome\Application\chrome.exe" },
                "edge" => new[] { @"Microsoft\Edge\Application\msedge.exe" },
                "brave" => new[] { @"BraveSoftware\Brave-Browser\Application\brave.exe" },
                "vivaldi" => new[] { @"Vivaldi\Application\vivaldi.exe" },
                "chromium" => new[] { @"Chromium\Application\chrome.exe" },
                _ => Array.Empty<string>()
            };
            candidates = relative.SelectMany(r => new[] { Path.Combine(programs, r), Path.Combine(programs86, r), Path.Combine(local, r) }).ToArray();
        }
        else if (OperatingSystem.IsMacOS())
        {
            string? app = id switch
            {
                "firefox" => "Firefox.app/Contents/MacOS/firefox",
                "chrome" => "Google Chrome.app/Contents/MacOS/Google Chrome",
                "edge" => "Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                "brave" => "Brave Browser.app/Contents/MacOS/Brave Browser",
                "vivaldi" => "Vivaldi.app/Contents/MacOS/Vivaldi",
                "chromium" => "Chromium.app/Contents/MacOS/Chromium",
                _ => null
            };
            candidates = app == null ? Array.Empty<string>() : new[] { Path.Combine("/Applications", app), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", app) };
        }
        else
        {
            string[] names = id switch
            {
                "firefox" => new[] { "firefox", "firefox-esr" },
                "chrome" => new[] { "google-chrome", "google-chrome-stable" },
                "edge" => new[] { "microsoft-edge", "microsoft-edge-stable" },
                "brave" => new[] { "brave-browser", "brave" },
                "vivaldi" => new[] { "vivaldi", "vivaldi-stable" },
                "chromium" => new[] { "chromium", "chromium-browser" },
                _ => Array.Empty<string>()
            };
            candidates = names.SelectMany(n => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Select(f => Path.Combine(f, n))).ToArray();
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}

/// <summary>
/// The fallback for browsers that refuse to share their cookies: a separate window with its own profile, where the
/// person signs in once. Their everyday profile, tabs and passwords stay out of it.
/// </summary>
public static class SeparateSignIn
{
    const string SignInUrl = "https://accounts.google.com/ServiceLogin?service=youtube&uilel=3&passive=true"
        + "&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26next%3D%252F";

    // A fresh Firefox profile would otherwise greet with welcome pages and offer to remember the Google password.
    const string FirefoxPrefs =
        "user_pref(\"browser.shell.checkDefaultBrowser\", false);\n" +
        "user_pref(\"browser.aboutwelcome.enabled\", false);\n" +
        "user_pref(\"browser.startup.homepage_override.mstone\", \"ignore\");\n" +
        "user_pref(\"startup.homepage_welcome_url\", \"\");\n" +
        "user_pref(\"startup.homepage_welcome_url.additional\", \"\");\n" +
        "user_pref(\"datareporting.policy.dataSubmissionPolicyBypassNotification\", true);\n" +
        "user_pref(\"toolkit.telemetry.reportingpolicy.firstRun\", false);\n" +
        "user_pref(\"datareporting.healthreport.uploadEnabled\", false);\n" +
        "user_pref(\"signon.rememberSignons\", false);\n";

    /// <summary>Opens the window and waits until the sign-in is done or the window is closed.</summary>
    public static Task<SignInCheck> RunAsync(string browser, CancellationToken cancel)
    {
        string exe = Browsers.Executable(browser) ?? throw new FileNotFoundException(Browsers.DisplayName(browser));
        Directory.CreateDirectory(AppPaths.SignInProfile);
        return Browsers.IsChromium(browser) ? RunChromiumAsync(browser, exe, cancel) : RunFirefoxAsync(exe, cancel);
    }

    static Process Launch(string exe, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("The browser did not start.");
    }

    // Firefox stores new cookies on disk when the window closes, so that is the moment to read them.
    static async Task<SignInCheck> RunFirefoxAsync(string exe, CancellationToken cancel)
    {
        File.WriteAllText(Path.Combine(AppPaths.SignInProfile, "user.js"), FirefoxPrefs, new UTF8Encoding(false));
        // -no-remote runs next to an open everyday Firefox; -wait-for-browser keeps this process until the window closes.
        using Process process = Launch(exe, new[] { "-no-remote", "-wait-for-browser", "-profile", AppPaths.SignInProfile, "-new-window", SignInUrl });
        try { await process.WaitForExitAsync(cancel).ConfigureAwait(false); }
        catch (OperationCanceledException) { TryClose(process); throw; }
        var check = await Auth.CheckBrowserAsync("firefox", AppPaths.SignInProfile, cancel).ConfigureAwait(false);
        if (check.Status == SignInStatus.SignedIn) Auth.UseBrowser("firefox", AppPaths.SignInProfile);
        return check;
    }

    // A browser's cookie store is out of reach here, but its own debugging port can hand them over.
    // The port is local, used only while this window is open, and requires this separate profile anyway.
    static async Task<SignInCheck> RunChromiumAsync(string browser, string exe, CancellationToken cancel)
    {
        int port = FreePort();
        using Process process = Launch(exe, new[] { "--user-data-dir=" + AppPaths.SignInProfile, "--remote-debugging-port=" + port.ToString(CultureInfo.InvariantCulture),
            "--no-first-run", "--no-default-browser-check", "--new-window", SignInUrl });
        try
        {
            while (true)
            {
                await Task.Delay(2000, cancel).ConfigureAwait(false);
                if (process.HasExited) return new SignInCheck(SignInStatus.NotSignedIn);
                List<string> lines;
                try { lines = await ReadCookiesAsync(port, false, cancel).ConfigureAwait(false); }
                catch (Exception ex) when (!cancel.IsCancellationRequested && ex is WebSocketException or HttpRequestException or InvalidDataException or OperationCanceledException) { continue; }
                if (!lines.Any(Auth.IsLoginLine)) continue;
                // Give YouTube a moment to finish setting its cookies, then read them again and close the window.
                await Task.Delay(2000, cancel).ConfigureAwait(false);
                lines = await ReadCookiesAsync(port, true, cancel).ConfigureAwait(false);
                Auth.WriteCookieFile(lines);
                Auth.UseCookieFile();
                return new SignInCheck(SignInStatus.SignedIn);
            }
        }
        catch (OperationCanceledException) { TryClose(process); throw; }
    }

    static void TryClose(Process process)
    {
        try { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(3000)) process.Kill(true); } }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>The youtube.com cookies, as cookies.txt lines.</summary>
    static async Task<List<string>> ReadCookiesAsync(int port, bool closeBrowser, CancellationToken cancel)
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        string version = await client.GetStringAsync("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/json/version", cancel).ConfigureAwait(false);
        string socketUrl = Json.ParseObject(version).Str("webSocketDebuggerUrl") ?? throw new InvalidDataException("The browser did not answer.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(socketUrl), timeout.Token).ConfigureAwait(false);
        await SendAsync(socket, 1, "Storage.getCookies", timeout.Token).ConfigureAwait(false);
        var buffer = new byte[65536];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(false);
                if (part.MessageType == WebSocketMessageType.Close) throw new InvalidDataException("The browser closed the connection.");
                message.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);
            var reply = Json.ParseObject(Encoding.UTF8.GetString(message.ToArray()));
            if (reply.Num("id") != 1) continue;
            var cookies = (reply.Raw("result") as Dictionary<string, object?>).Raw("cookies") as List<object?> ?? throw new InvalidDataException("The browser gave no cookies.");
            if (closeBrowser) { try { await SendAsync(socket, 2, "Browser.close", timeout.Token).ConfigureAwait(false); } catch (WebSocketException) { } }
            return cookies.OfType<Dictionary<string, object?>>().Select(ToLine).OfType<string>().ToList();
        }
    }

    static Task SendAsync(ClientWebSocket socket, int id, string method, CancellationToken token)
    {
        byte[] request = Encoding.UTF8.GetBytes("{\"id\":" + id.ToString(CultureInfo.InvariantCulture) + ",\"method\":\"" + method + "\"}");
        return socket.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, true, token);
    }

    /// <summary>One browser cookie as a cookies.txt line, or null if it is not for YouTube. Public for tests.</summary>
    public static string? ToLine(Dictionary<string, object?> cookie)
    {
        string? domain = cookie.Str("domain"), name = cookie.Str("name");
        if (domain == null || name == null || !(domain == "youtube.com" || domain.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase))) return null;
        double expires = cookie.Num("expires") ?? 0;
        return (cookie.Flag("httpOnly") ? "#HttpOnly_" : "") + domain + "\t" + (domain.StartsWith('.') ? "TRUE" : "FALSE") + "\t" + (cookie.Str("path") ?? "/") + "\t"
            + (cookie.Flag("secure") ? "TRUE" : "FALSE") + "\t" + (expires > 0 ? (long)expires : 0).ToString(CultureInfo.InvariantCulture) + "\t" + name + "\t" + (cookie.Str("value") ?? "");
    }
}
