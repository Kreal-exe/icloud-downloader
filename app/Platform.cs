using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace iCloudDownloader;

/// <summary>Where things live on Windows and macOS, and the few things each system does its own way.</summary>
public static class Platform
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMac => OperatingSystem.IsMacOS();

    /// <summary>%APPDATA%\iCloudDownloader or ~/Library/Application Support/iCloudDownloader.</summary>
    public static string DataDir
    {
        get
        {
            var root = IsMac
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            // ICDL_DATA: a separate profile (tests, a second account).
            var dir = Environment.GetEnvironmentVariable("ICDL_DATA") is { Length: > 0 } custom ? custom : Path.Combine(root, "iCloudDownloader");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>The saved iCloud sign-in (cookies and session token), kept between runs.</summary>
    public static string SessionDir
    {
        get
        {
            var dir = Path.Combine(DataDir, "session");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string LogDir
    {
        get
        {
            var dir = Path.Combine(DataDir, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static bool HasSession(string user)
    {
        var name = new string(user.Where(char.IsLetterOrDigit).ToArray());
        return name.Length > 0 && File.Exists(Path.Combine(SessionDir, name + ".session"));
    }

    /// <summary>
    /// The engine. A release carries it as one executable (engine\icdl-engine.exe next to the app, or
    /// Contents/Resources/engine/icdl-engine in the macOS bundle): Script is then null. A build from source runs
    /// icdl_engine.py with the checkout's .venv, looked for above the app and in the repository it was built from.
    /// </summary>
    public static (string Program, string? Script)? FindEngine()
    {
        var configured = Environment.GetEnvironmentVariable("ICDL_ROOT");
        if (string.IsNullOrEmpty(configured))
        {
            var bundled = IsWindows
                ? Path.Combine(AppContext.BaseDirectory, "engine", "icdl-engine.exe")
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "engine", "icdl-engine"));
            if (File.Exists(bundled)) return (bundled, null);
        }

        var starts = new List<string> { AppContext.BaseDirectory };
        if (IsMac) starts.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "engine")));
        if (!string.IsNullOrEmpty(configured)) starts.Insert(0, configured);
        starts.Add(BuildInfo.RepoRoot);

        foreach (var start in starts)
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                var script = Path.Combine(dir.FullName, "src", "icdl_engine.py");
                if (!File.Exists(script)) continue;
                var python = IsWindows
                    ? Path.Combine(dir.FullName, ".venv", "Scripts", "python.exe")
                    : Path.Combine(dir.FullName, ".venv", "bin", "python3");
                if (File.Exists(python)) return (python, script);
            }
        }
        return null;
    }

    // ------------------------------------------------------------ keep the computer awake while downloading

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    static Process? _caffeinate;

    public static void KeepAwake(bool on)
    {
        try
        {
            if (IsWindows)
            {
                const uint Continuous = 0x80000000, SystemRequired = 0x00000001;
                SetThreadExecutionState(on ? Continuous | SystemRequired : Continuous);
            }
            else if (IsMac)
            {
                if (on && _caffeinate is not { HasExited: false })
                    _caffeinate = Process.Start(new ProcessStartInfo("/usr/bin/caffeinate", $"-i -w {Environment.ProcessId}") { UseShellExecute = false });
                else if (!on && _caffeinate is { HasExited: false })
                    _caffeinate.Kill();
            }
        }
        catch
        {
            // Not being able to keep the computer awake is no reason to stop.
        }
    }

    public static void OpenFolder(string path)
    {
        try
        {
            if (IsWindows) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (IsMac) Process.Start("open", $"\"{path}\"");
        }
        catch
        {
        }
    }
}

/// <summary>What the user chose last time.</summary>
public sealed class Settings
{
    public string User { get; set; } = "";
    /// <summary>The account owner's name, as Apple reports it after signing in.</summary>
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public int Workers { get; set; } = 6;
    public bool AutoRestart { get; set; } = true;
    public bool KeepAwake { get; set; } = true;
    /// <summary>Videos already in the folder count by name, whatever their size: compressed ones are not downloaded again.</summary>
    public bool VideosByName { get; set; } = true;
    /// <summary>Also download the Hidden album, into its own folder (HiddenFolder) inside the destination.</summary>
    public bool DownloadHidden { get; set; }
    public const string HiddenFolder = "Hidden";
    /// <summary>"ru" or "en"; empty: the system's language.</summary>
    public string Language { get; set; } = "";
    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; set; } = "system";

    static string FilePath => Path.Combine(Platform.DataDir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
        }
        return new Settings
        {
            Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "iCloud Photos"),
        };
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
