using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Volna.Core;

public sealed record RunResult(int ExitCode, bool Cancelled);

/// <summary>Runs one external program, streams its output, and can stop it together with its children.</summary>
public sealed class ProcessRunner : IDisposable
{
    readonly object gate = new();
    Process? process;
    bool cancelled;

    public Task<RunResult> RunAsync(string exe, IEnumerable<string> args, Action<string> output, IDictionary<string, string>? environment = null)
    {
        return Task.Run(() =>
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                // Children must never read our stdin: for the agent it carries the MCP conversation.
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = AppPaths.Data
            };
            foreach (string arg in args) info.ArgumentList.Add(arg);
            info.Environment["PYTHONIOENCODING"] = "utf-8";
            info.Environment["PYTHONUTF8"] = "1";
            if (environment != null) foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;

            lock (gate) { if (cancelled) return new RunResult(-1, true); }
            using var started = new Process { StartInfo = info };
            started.OutputDataReceived += (_, e) => { if (e.Data != null) Deliver(output, e.Data); };
            started.ErrorDataReceived += (_, e) => { if (e.Data != null) Deliver(output, e.Data); };
            started.Start();
            using var job = OperatingSystem.IsWindows() ? new WindowsJob(started) : null;
            lock (gate) { process = started; if (cancelled) Kill(started); }
            try { started.StandardInput.Close(); } catch (IOException) { }
            started.BeginOutputReadLine();
            started.BeginErrorReadLine();
            started.WaitForExit();
            lock (gate)
            {
                process = null;
                return new RunResult(started.ExitCode, cancelled);
            }
        });
    }

    // A reader callback that throws must not take the whole app down with it.
    static void Deliver(Action<string> output, string line)
    {
        try { output(line); }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    static void Kill(Process target)
    {
        try { target.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Cancel()
    {
        Process? victim;
        lock (gate) { cancelled = true; victim = process; }
        // Stopping a process tree can take a moment and must not block the caller's thread.
        if (victim != null) Task.Run(() => Kill(victim));
    }

    public void Dispose() => Cancel();
}

/// <summary>Windows only: children die with this process even if it crashes. Elsewhere Cancel() handles the tree.</summary>
[SupportedOSPlatform("windows")]
sealed class WindowsJob : IDisposable
{
    IntPtr handle;

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    public WindowsJob(Process process)
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return;
        var limits = new ExtendedLimits();
        limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>());
        AssignProcessToJobObject(handle, process.Handle);
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        CloseHandle(handle);
        handle = IntPtr.Zero;
    }
}

public static class Desktop
{
    /// <summary>Opens a file, folder or link with whatever the system uses for it (the default browser for a link).</summary>
    public static void Open(string target)
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        else Start(OperatingSystem.IsMacOS() ? "open" : "xdg-open", target);
    }

    /// <summary>Shows a file selected in the file manager; where that is not possible, opens its folder.</summary>
    public static void Reveal(string file)
    {
        if (OperatingSystem.IsWindows()) Start("explorer.exe", "/select," + file);
        else if (OperatingSystem.IsMacOS()) Start("open", "-R", file);
        else Open(Path.GetDirectoryName(file) ?? file);
    }

    public static void PlayDoneSound(bool failed)
    {
        try
        {
            if (OperatingSystem.IsWindows()) MessageBeep(failed ? 0x30u : 0x40u);
            else if (OperatingSystem.IsMacOS()) Start("afplay", failed ? "/System/Library/Sounds/Basso.aiff" : "/System/Library/Sounds/Glass.aiff");
            else Start("canberra-gtk-play", "-i", failed ? "dialog-warning" : "complete");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { } // a sound is never worth an error
    }

    [DllImport("user32.dll")] static extern bool MessageBeep(uint type);

    static void Start(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        Process.Start(info)?.Dispose();
    }
}
