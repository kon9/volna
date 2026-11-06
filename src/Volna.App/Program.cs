using System.IO.Pipes;
using System.Text;
using Avalonia;
using Avalonia.Threading;
using Volna.Core;

namespace Volna.App;

public static class Program
{
    public static List<string> StartupEntries { get; private set; } = new();

    static FileStream? instanceLock;

    // Per data folder: a portable copy with its own data is a separate app.
    static string PipeName => "volna-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.Data)))[..16];

    /// <summary>Links a second start of the app handed over to this one.</summary>
    public static event Action<string>? EntriesFromOtherInstance;

    [STAThread]
    public static int Main(string[] args)
    {
        string text = string.Join("\n", args);
        try
        {
            if (!TakeInstance())
            {
                HandOver(text);
                return 0;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Nothing can be saved, and with no console a program that just ends tells the person nothing.
            Loc.Use("auto");
            Fatal(Loc.T("fatal.data", ex.Message));
            return 1;
        }
        StartupEntries = args.ToList();
        Listen();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    static void Fatal(string message)
    {
        if (OperatingSystem.IsWindows()) _ = MessageBoxW(IntPtr.Zero, message, "Volna", 0x10);
        else Console.Error.WriteLine(message);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    // One window per person: two would fight over the queue file and the download folder.
    static bool TakeInstance()
    {
        try
        {
            instanceLock = new FileStream(Path.Combine(AppPaths.Data, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException ex) when (File.Exists(Path.Combine(AppPaths.Data, "instance.lock")) && ex is not DirectoryNotFoundException) { return false; }
    }

    static void HandOver(string text)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            byte[] data = Encoding.UTF8.GetBytes(text);
            pipe.Write(data);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { }
    }

    static void Listen()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync();
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    string text = await reader.ReadToEndAsync();
                    Dispatcher.UIThread.Post(() => EntriesFromOtherInstance?.Invoke(text));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(1000);
                }
            }
        });
    }
}
