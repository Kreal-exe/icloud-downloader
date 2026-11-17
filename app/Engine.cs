using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace iCloudDownloader;

/// <summary>
/// Runs icdl_engine.py and turns its JSON lines into events on the calling (UI) thread. A watchdog stops an engine
/// that has said nothing for a while, which is how the old script could hang for hours.
/// </summary>
public sealed class Engine
{
    public static readonly TimeSpan Silence = TimeSpan.FromMinutes(4);

    readonly SynchronizationContext _ui;
    Process? _process;
    readonly Queue<string> _stderr = new();
    DateTime _lastLine;
    Timer? _watchdog;
    bool _stopping;
    bool _waitingForUser;

    public event Action<JsonElement>? Message;
    /// <summary>The engine ended: its exit code (-1 when killed by the watchdog) and the last lines of its stderr.</summary>
    public event Action<int, string>? Exited;

    public Engine(SynchronizationContext ui) => _ui = ui;

    public bool Running => _process is { HasExited: false };

    /// <summary>mode: "download", "login" or "logout".</summary>
    public string? Start(string mode, string user, string folder, int workers, params string[] extra)
    {
        var engine = Platform.FindEngine();
        if (engine == null)
            return Loc.Tr("Не найден движок загрузки (папка .venv и src/icdl_engine.py). Запустите build.ps1 (Windows) или build-mac.sh (macOS) в папке проекта.",
                "The download engine was not found (.venv and src/icdl_engine.py). Run build.ps1 (Windows) or build-mac.sh (macOS) in the project folder.");

        var (program, script) = engine.Value;
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(script ?? program)!,
        };
        info.StandardInputEncoding = new UTF8Encoding(false);
        // From source: python -u icdl_engine.py …; a release's engine is one executable.
        if (script != null)
        {
            info.ArgumentList.Add("-u");
            info.ArgumentList.Add(script);
        }
        foreach (var arg in new[] { "--mode", mode, "--user", user, "--dir", folder, "--cookies", Platform.SessionDir,
                     "--workers", workers.ToString(), "--lang", Loc.Language })
            info.ArgumentList.Add(arg);
        foreach (var arg in extra) info.ArgumentList.Add(arg);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUNBUFFERED"] = "1";

        _stderr.Clear();
        _stopping = false;
        _waitingForUser = false;
        try
        {
            _process = new Process { StartInfo = info, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) => { if (e.Data != null) OnLine(e.Data); };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_stderr)
                {
                    _stderr.Enqueue(e.Data);
                    while (_stderr.Count > 40) _stderr.Dequeue();
                }
                _lastLine = DateTime.UtcNow;
            };
            _process.Exited += (_, _) => OnExit();
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        catch (Exception error)
        {
            return Loc.Tr($"Не удалось запустить движок: {error.Message}", $"Could not start the engine: {error.Message}");
        }

        _lastLine = DateTime.UtcNow;
        _watchdog = new Timer(_ => CheckSilence(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        return null;
    }

    void OnLine(string line)
    {
        _lastLine = DateTime.UtcNow;
        if (!line.StartsWith('{'))
        {
            lock (_stderr) _stderr.Enqueue(line);
            return;
        }
        JsonElement message;
        try
        {
            message = JsonDocument.Parse(line).RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }
        var kind = message.TryGetProperty("event", out var e) ? e.GetString() : null;
        _waitingForUser = kind is "need_password" or "need_code";
        _ui.Post(_ => Message?.Invoke(message), null);
    }

    void CheckSilence()
    {
        // Waiting for a password or a code is not hanging.
        if (_waitingForUser || !Running) return;
        if (DateTime.UtcNow - _lastLine < Silence) return;
        lock (_stderr) _stderr.Enqueue(Loc.Tr($"Движок молчал больше {Silence.TotalMinutes:0} минут — остановлен сторожевым таймером.",
            $"The engine was silent for over {Silence.TotalMinutes:0} minutes and was stopped by the watchdog."));
        Kill();
    }

    void OnExit()
    {
        _watchdog?.Dispose();
        _watchdog = null;
        int code;
        try
        {
            // Let the last lines of output arrive first.
            _process?.WaitForExit();
            code = _process?.ExitCode ?? -1;
        }
        catch
        {
            code = -1;
        }
        if (_stopping) code = -2;
        string tail;
        lock (_stderr) tail = string.Join("\n", _stderr);
        _ui.Post(_ => Exited?.Invoke(code, tail), null);
    }

    /// <summary>A command while running ({ workers = 8 }): unlike an answer, it does not end a wait for the user.</summary>
    public void Send(object command)
    {
        var waiting = _waitingForUser;
        Answer(command);
        _waitingForUser = waiting;
    }

    public void Answer(object answer)
    {
        if (!Running) return;
        _waitingForUser = false;
        _lastLine = DateTime.UtcNow;
        try
        {
            _process!.StandardInput.WriteLine(JsonSerializer.Serialize(answer));
            _process.StandardInput.Flush();
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Stops the engine. Half-downloaded files stay as .part and are resumed next time.</summary>
    public void Stop()
    {
        _stopping = true;
        Kill();
    }

    void Kill()
    {
        try
        {
            if (Running) _process!.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}
