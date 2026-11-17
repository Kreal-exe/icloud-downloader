using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using iCloudDownloader.Controls;

namespace iCloudDownloader;

public sealed class LogItem
{
    public string Time { get; init; } = "";
    public string Text { get; init; } = "";
    public string? Detail { get; init; }
    public string Level { get; init; } = "info";
    public bool IsError => Level is "error" or "warning";
    // Colours come from style classes, so they follow a theme change.
    public bool LevelError => Level == "error";
    public bool LevelWarning => Level == "warning";
    public bool LevelOk => Level == "ok";
    public bool LevelFile => Level == "file";
}

public sealed class ActiveItem
{
    public string Name { get; init; } = "";
    public string Info { get; init; } = "";
    public double Fraction { get; init; }
}

public sealed partial class MainWindow : Window
{
    readonly Settings _settings = Settings.Load();
    readonly Engine _engine;
    readonly Engine _loginEngine;
    readonly ObservableCollection<LogItem> _log = [];
    readonly ObservableCollection<LogItem> _errors = [];
    readonly ObservableCollection<ActiveItem> _active = [];
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly string _logFile;

    // the run
    bool _running, _userStopped, _stallKill, _gotDone, _fatalNoRetry, _fatalAuth;
    string? _fatalMessage, _fatalDetail;
    long _total, _done, _existed, _downloaded, _failed, _bytes;
    long _lastSignature = -1;
    DateTime _lastChange, _startedAt;
    readonly Queue<(DateTime At, long Bytes, long Done)> _samples = new();

    // automatic restarts
    int _restarts, _failedRounds;
    DateTime? _restartAt;

    // sign-in
    Engine? _authTarget;          // the engine waiting for the password or code
    string? _pendingPassword;     // typed in the dialog, sent when the login engine asks
    bool _pendingRemember, _loginOk, _startAfterLogin, _loginCancelled;
    string? _loginFatal;
    bool _scrollPending;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        var ui = SynchronizationContext.Current!;
        _engine = new Engine(ui);
        _engine.Message += OnMessage;
        _engine.Exited += OnExited;
        _loginEngine = new Engine(ui);
        _loginEngine.Message += OnLoginMessage;
        _loginEngine.Exited += OnLoginExited;
        _logFile = Path.Combine(Platform.LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");

        ApplyLanguage(_settings.Language.Length > 0 ? _settings.Language
            : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en");
        ApplyTheme(_settings.Theme);

        Get<TextBox>("FolderBox").Text = _settings.Folder;
        Get<NumericUpDown>("WorkersBox").Value = _settings.Workers;
        Get<ListBox>("LogList").ItemsSource = _log;
        Get<ItemsControl>("ActiveList").ItemsSource = _active;
        Get<TextBlock>("VersionText").Text = $"iCloud Downloader {BuildInfo.Version} · icloudpd 1.33.1";

        Get<Button>("StartButton").Click += (_, _) => Start(manual: true);
        Get<Button>("StopButton").Click += (_, _) => StopByUser();
        Get<Button>("RetryButton").Click += (_, _) => Start(manual: true);
        Get<Button>("BrowseButton").Click += async (_, _) => await BrowseAsync();
        Get<Button>("OpenFolderButton").Click += (_, _) => Platform.OpenFolder(Get<TextBox>("FolderBox").Text ?? "");
        Get<Button>("CopyLogButton").Click += async (_, _) => await CopyLogAsync();
        Get<CheckBox>("ErrorsOnlyBox").IsCheckedChanged += (_, _) =>
            Get<ListBox>("LogList").ItemsSource = Get<CheckBox>("ErrorsOnlyBox").IsChecked == true ? _errors : _log;
        Get<NumericUpDown>("WorkersBox").ValueChanged += (_, _) => WorkersChanged();

        // account
        Get<Button>("SignInButton").Click += (_, _) => OpenLogin(startAfter: false);
        Get<Button>("SignOutButton").Click += (_, _) => SignOut();
        Get<Button>("LoginOk").Click += (_, _) => SubmitLogin();
        Get<Button>("LoginCancel").Click += (_, _) => CancelLogin();
        Get<TextBox>("CodeBox").TextChanged += (_, _) =>
        {
            var code = new string((Get<TextBox>("CodeBox").Text ?? "").Where(char.IsDigit).ToArray());
            if (code.Length == 6 && Get<StackPanel>("CodeStep").IsVisible && !Get<TextBlock>("LoginBusy").IsVisible) SubmitLogin();
        };

        // settings
        Get<Button>("ThemeButton").Click += (_, _) =>
        {
            _settings.Theme = ActualThemeVariant == ThemeVariant.Dark ? "light" : "dark";
            _settings.Save();
            ApplyTheme(_settings.Theme);
        };
        ActualThemeVariantChanged += (_, _) => UpdateThemeIcon();
        Get<Button>("SettingsButton").Click += (_, _) => OpenSettings();
        Get<Button>("SettingsClose").Click += (_, _) => Get<Grid>("SettingsOverlay").IsVisible = false;
        Get<Button>("LogsButton").Click += (_, _) => Platform.OpenFolder(Platform.LogDir);
        Get<ComboBox>("LanguageBox").SelectionChanged += (_, _) =>
        {
            if (!Get<Grid>("SettingsOverlay").IsVisible) return;
            _settings.Language = Get<ComboBox>("LanguageBox").SelectedIndex == 1 ? "en" : "ru";
            _settings.Save();
            ApplyLanguage(_settings.Language);
        };
        Get<ComboBox>("ThemeBox").SelectionChanged += (_, _) =>
        {
            if (!Get<Grid>("SettingsOverlay").IsVisible) return;
            _settings.Theme = Get<ComboBox>("ThemeBox").SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
            _settings.Save();
            ApplyTheme(_settings.Theme);
        };
        Get<CheckBox>("AutoRestartBox").IsCheckedChanged += (_, _) =>
        {
            _settings.AutoRestart = Get<CheckBox>("AutoRestartBox").IsChecked == true;
            _settings.Save();
        };
        Get<CheckBox>("HiddenBox").IsCheckedChanged += (_, _) =>
        {
            _settings.DownloadHidden = Get<CheckBox>("HiddenBox").IsChecked == true;
            _settings.Save();
        };
        Get<CheckBox>("VideosByNameBox").IsCheckedChanged += (_, _) =>
        {
            _settings.VideosByName = Get<CheckBox>("VideosByNameBox").IsChecked == true;
            _settings.Save();
        };
        Get<CheckBox>("KeepAwakeBox").IsCheckedChanged += (_, _) =>
        {
            _settings.KeepAwake = Get<CheckBox>("KeepAwakeBox").IsChecked == true;
            _settings.Save();
            Platform.KeepAwake(_running && _settings.KeepAwake);
        };

        _clock.Tick += (_, _) => Tick();
        _clock.Start();
        UpdateAccount();
        SetReady();
        Log("info", Loc.Tr($"iCloud Downloader {BuildInfo.Version}. Журнал сохраняется (Настройки → Папка журналов).", $"iCloud Downloader {BuildInfo.Version}. The log is saved (Settings → Logs folder)."));
        if (Platform.FindEngine() == null)
            ShowProblem(Loc.Tr("Не найден движок загрузки.", "The download engine was not found."),
                Loc.Tr("Запустите build.ps1 (Windows) или build-mac.sh (macOS) в папке проекта.", "Run build.ps1 (Windows) or build-mac.sh (macOS) in the project folder."), retry: false);
    }

    T Get<T>(string name) where T : Control => this.FindControl<T>(name)!;

    IBrush? Brush(string key) =>
        Application.Current!.TryGetResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    // ------------------------------------------------------------------ window

    /// <summary>Never taller or wider than the screen's working area (laptops with 125–150 % scaling).</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen != null)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            Width = Math.Min(Width, area.Width / scale * 0.92);
            Height = Math.Min(Height, area.Height / scale * 0.90);
            Position = new PixelPoint(
                area.X + (int)((area.Width - Width * scale) / 2),
                area.Y + (int)((area.Height - Height * scale) / 2));
        }
        // "iCloudDownloader --start" begins downloading right away (a shortcut or a scheduled task).
        if (Environment.GetCommandLineArgs().Contains("--start")) Start(manual: true);
        else if (!SignedIn) OpenLogin(startAfter: false);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Shutdown();
        base.OnClosing(e);
    }

    public void Shutdown()
    {
        _userStopped = true;
        _engine.Stop();
        _loginEngine.Stop();
        Platform.KeepAwake(false);
    }

    void ApplyLanguage(string language)
    {
        Loc.Set(language);
        if (!_running && _restartAt == null) SetReady();
        UpdateAccount();
        UpdateStats();
        if (Get<Grid>("LoginOverlay").IsVisible) UpdateLoginTexts();
    }

    void ApplyTheme(string theme)
    {
        Application.Current!.RequestedThemeVariant = theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        UpdateThemeIcon();
    }

    /// <summary>The moon in the light theme (switch to dark), the sun in the dark one; log colours follow.</summary>
    void UpdateThemeIcon()
    {
        Get<MaterialIcon>("ThemeIcon").Kind = ActualThemeVariant == ThemeVariant.Dark ? MaterialIconKind.WhiteBalanceSunny : MaterialIconKind.WeatherNight;
        UpdateStats();
    }

    void OpenSettings()
    {
        Get<ComboBox>("LanguageBox").SelectedIndex = Loc.En ? 1 : 0;
        Get<ComboBox>("ThemeBox").SelectedIndex = _settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        Get<CheckBox>("AutoRestartBox").IsChecked = _settings.AutoRestart;
        Get<CheckBox>("KeepAwakeBox").IsChecked = _settings.KeepAwake;
        Get<CheckBox>("VideosByNameBox").IsChecked = _settings.VideosByName;
        Get<CheckBox>("HiddenBox").IsChecked = _settings.DownloadHidden;
        Get<Grid>("SettingsOverlay").IsVisible = true;
    }

    // ------------------------------------------------------------------ account

    bool SignedIn => _settings.User.Length > 0 && Platform.HasSession(_settings.User);

    void UpdateAccount()
    {
        var signedIn = SignedIn;
        Get<Border>("AccountChip").IsVisible = signedIn;
        Get<Button>("SignInButton").IsVisible = !signedIn;
        if (!signedIn) return;
        var user = _settings.User;
        Get<TextBlock>("AccountName").Text = _settings.Name is { Length: > 0 } name ? $"{name} · {user}" : user;
        Get<TextBlock>("AvatarText").Text = ((_settings.Name is { Length: > 0 } n ? n : user)[..1]).ToUpperInvariant();
        Get<Button>("SignOutButton").IsEnabled = !_running;
    }

    void SignOut()
    {
        if (_running) return;
        var user = _settings.User;
        var error = _loginEngine.Start("logout", user, "", 1);
        if (error != null)
        {
            // No engine: forget the session by hand.
            foreach (var file in Directory.GetFiles(Platform.SessionDir)) File.Delete(file);
        }
        _settings.Name = "";
        _settings.Save();
        Log("info", Loc.Tr($"Выход из {user}: сохранённый вход и пароль удалены.", $"Signed out of {user}: the saved session and password were removed."));
        DispatcherTimer.RunOnce(UpdateAccount, TimeSpan.FromSeconds(1.5));
    }

    void OpenLogin(bool startAfter)
    {
        _startAfterLogin = startAfter;
        _authTarget = null;
        _loginOk = false;
        Get<TextBox>("LoginUserBox").Text = _settings.User;
        Get<TextBox>("LoginUserBox").IsEnabled = true;
        Get<TextBox>("PasswordBox").Text = "";
        Get<CheckBox>("RememberBox").IsChecked = true;
        ShowLoginStep(code: false, error: "");
        Get<Grid>("LoginOverlay").IsVisible = true;
        Dispatcher.UIThread.Post(() =>
            (string.IsNullOrEmpty(_settings.User) ? Get<TextBox>("LoginUserBox") : Get<TextBox>("PasswordBox")).Focus(), DispatcherPriority.Input);
    }

    void ShowLoginStep(bool code, string error)
    {
        Get<StackPanel>("CredentialsStep").IsVisible = !code;
        Get<StackPanel>("CodeStep").IsVisible = code;
        Get<Border>("LoginErrorBox").IsVisible = error.Length > 0;
        Get<TextBlock>("LoginError").Text = error;
        SetLoginBusy(false);
        if (code) Get<TextBox>("CodeBox").Text = "";
        UpdateLoginTexts();
        Dispatcher.UIThread.Post(() => (code ? Get<TextBox>("CodeBox") : Get<TextBox>("PasswordBox")).Focus(), DispatcherPriority.Input);
    }

    void UpdateLoginTexts()
    {
        var code = Get<StackPanel>("CodeStep").IsVisible;
        Get<TextBlock>("LoginTitle").Text = Loc.T(code ? "code.title" : "login.title");
        Get<TextBlock>("LoginOkText").Text = Loc.T(code ? "code.submit" : "login.submit");
        Get<MaterialIcon>("LoginIcon").Kind = code ? MaterialIconKind.CellphoneKey : MaterialIconKind.AppleIcloud;
    }

    void SetLoginBusy(bool busy)
    {
        Get<TextBlock>("LoginBusy").IsVisible = busy;
        Get<Button>("LoginOk").IsEnabled = !busy;
        Get<TextBox>("PasswordBox").IsEnabled = !busy;
        Get<TextBox>("CodeBox").IsEnabled = !busy;
    }

    void SubmitLogin()
    {
        if (Get<StackPanel>("CodeStep").IsVisible)
        {
            var code = new string((Get<TextBox>("CodeBox").Text ?? "").Where(char.IsDigit).ToArray());
            if (code.Length != 6)
            {
                ShowLoginStep(code: true, Loc.Tr("Код — это 6 цифр.", "The code is 6 digits."));
                return;
            }
            SetLoginBusy(true);
            (_authTarget ?? _loginEngine).Answer(new { code });
            return;
        }

        var user = (Get<TextBox>("LoginUserBox").Text ?? "").Trim();
        var password = Get<TextBox>("PasswordBox").Text ?? "";
        if (!user.Contains('@'))
        {
            ShowLoginStep(code: false, Loc.Tr("Введите Apple ID — это почта, с которой вы входите в iCloud.", "Enter your Apple ID — the email you use for iCloud."));
            return;
        }
        if (password.Length == 0)
        {
            ShowLoginStep(code: false, Loc.Tr("Введите пароль Apple ID.", "Enter your Apple ID password."));
            return;
        }
        var remember = Get<CheckBox>("RememberBox").IsChecked == true;
        SetLoginBusy(true);
        Get<Border>("LoginErrorBox").IsVisible = false;

        // The download engine asked (its session expired while running): answer it.
        if (_authTarget != null)
        {
            _authTarget.Answer(new { password, remember });
            return;
        }
        // The login engine is already waiting for a retyped password.
        if (_loginEngine.Running)
        {
            _loginEngine.Answer(new { password, remember });
            return;
        }
        if (user != _settings.User)
        {
            _settings.User = user;
            _settings.Name = "";
            _settings.Save();
        }
        _pendingPassword = password;
        _pendingRemember = remember;
        _loginOk = _loginCancelled = false;
        _loginFatal = null;
        var error = _loginEngine.Start("login", user, "", 1);
        if (error != null) ShowLoginStep(code: false, error);
    }

    void CancelLogin()
    {
        Get<Grid>("LoginOverlay").IsVisible = false;
        _startAfterLogin = false;
        if (_authTarget != null) _authTarget.Answer(new { cancel = true });
        else if (_loginEngine.Running)
        {
            _loginCancelled = true;
            _loginEngine.Answer(new { cancel = true });
        }
    }

    void OnLoginMessage(JsonElement m)
    {
        switch (Str(m, "event"))
        {
            case "need_password":
                if (_pendingPassword != null && Str(m, "error").Length == 0)
                {
                    _loginEngine.Answer(new { password = _pendingPassword, remember = _pendingRemember });
                    _pendingPassword = null;
                }
                else
                {
                    _pendingPassword = null;
                    Get<Grid>("LoginOverlay").IsVisible = true;
                    Get<TextBox>("PasswordBox").Text = "";
                    ShowLoginStep(code: false, Str(m, "error"));
                }
                break;
            case "need_code":
                _pendingPassword = null;
                Get<Grid>("LoginOverlay").IsVisible = true;
                ShowLoginStep(code: true, Str(m, "error"));
                break;
            case "login_ok":
                OnSignedIn(Str(m, "name"));
                break;
            case "fatal":
                _loginFatal = Str(m, "message");
                break;
        }
    }

    void OnLoginExited(int code, string tail)
    {
        _pendingPassword = null;
        if (_loginOk || _loginCancelled)
        {
            UpdateAccount();
            return;
        }
        if (!Get<Grid>("LoginOverlay").IsVisible) return;   // a sign-out run
        ShowLoginStep(code: false, _loginFatal ?? Loc.Tr($"Вход не удался. {Short(tail)}", $"Sign-in failed. {Short(tail)}"));
    }

    void OnSignedIn(string name)
    {
        _loginOk = true;
        if (name.Length > 0) _settings.Name = name;
        _settings.Save();
        Get<Grid>("LoginOverlay").IsVisible = false;
        Log("ok", Loc.Tr($"Вход выполнен: {_settings.User}. Сессия сохранена.", $"Signed in: {_settings.User}. The session is saved."));
        UpdateAccount();
        HideProblem();
        if (_startAfterLogin && _authTarget == null)
        {
            _startAfterLogin = false;
            Start(manual: true);
        }
    }

    // ------------------------------------------------------------------ start / stop

    void Start(bool manual)
    {
        if (_engine.Running) return;
        if (!SignedIn)
        {
            OpenLogin(startAfter: true);
            return;
        }
        var folder = (Get<TextBox>("FolderBox").Text ?? "").Trim();
        if (folder.Length == 0)
        {
            ShowProblem(Loc.Tr("Выберите папку, куда скачивать.", "Choose the folder to download to."), "", retry: false);
            return;
        }
        var root = Path.GetPathRoot(Path.GetFullPath(folder));
        if (root != null && !Directory.Exists(root))
        {
            ShowProblem(Loc.Tr($"Диск {root} не найден. Подключите его или выберите другую папку.", $"Drive {root} was not found. Connect it or choose another folder."), "", retry: false);
            return;
        }

        _settings.Folder = folder;
        _settings.Workers = (int)(Get<NumericUpDown>("WorkersBox").Value ?? 6);
        _settings.Save();

        if (manual)
        {
            _restarts = 0;
            _failedRounds = 0;
        }
        _restartAt = null;
        _userStopped = _stallKill = _gotDone = _fatalNoRetry = _fatalAuth = false;
        _fatalMessage = _fatalDetail = null;
        _total = _done = _existed = _downloaded = _failed = _bytes = 0;
        _lastSignature = -1;
        _samples.Clear();
        _active.Clear();
        _startedAt = _lastChange = DateTime.UtcNow;
        HideProblem();

        var extra = new List<string>();
        if (_settings.VideosByName) extra.Add("--videos-by-name");
        if (_settings.DownloadHidden) extra.AddRange(["--hidden-dir", Settings.HiddenFolder]);
        var error = _engine.Start("download", _settings.User, folder, _settings.Workers, [.. extra]);
        if (error != null)
        {
            ShowProblem(error, "", retry: false);
            return;
        }
        _running = true;
        Platform.KeepAwake(_settings.KeepAwake);
        SetStatus(MaterialIconKind.CloudSyncOutline, Loc.Tr("Запуск…", "Starting…"), Loc.Tr("Подключаюсь к iCloud.", "Connecting to iCloud."));
        Log("info", manual
            ? Loc.Tr($"Старт: {_settings.User} → {folder}, потоков: {_settings.Workers}", $"Start: {_settings.User} → {folder}, parallel: {_settings.Workers}")
            : Loc.Tr($"Автоперезапуск №{_restarts}", $"Automatic restart #{_restarts}"));
        UpdateButtons();
    }

    void StopByUser()
    {
        _restartAt = null;
        if (!_engine.Running)
        {
            SetReady();
            UpdateButtons();
            return;
        }
        _userStopped = true;
        _engine.Stop();
        Log("info", Loc.Tr("Остановлено.", "Stopped."));
    }

    void WorkersChanged()
    {
        var workers = (int)(Get<NumericUpDown>("WorkersBox").Value ?? 6);
        _settings.Workers = workers;
        _settings.Save();
        if (_engine.Running) _engine.Send(new { workers });
    }

    // ------------------------------------------------------------------ engine messages

    void OnMessage(JsonElement m)
    {
        switch (Str(m, "event"))
        {
            case "status":
                var stage = Str(m, "stage");
                var icon = stage switch
                {
                    "login" => MaterialIconKind.LockOutline,
                    "counting" => MaterialIconKind.Counter,
                    "retry" => MaterialIconKind.Restart,
                    _ => MaterialIconKind.CloudDownloadOutline,
                };
                var detail = stage switch
                {
                    "running" => Loc.Tr("Файлы, которые уже есть в папке, пропускаются за доли секунды; недокачанные (.part) продолжаются с того же места.",
                        "Files already in the folder are skipped in a fraction of a second; unfinished ones (.part) resume where they stopped."),
                    "counting" => Loc.Tr("Спрашиваю у iCloud, сколько всего фото и видео.", "Asking iCloud how many photos and videos there are."),
                    _ => "",
                };
                SetStatus(icon, Str(m, "message"), detail);
                Log("info", Str(m, "message"));
                break;

            case "need_password":
                _authTarget = _engine;
                Get<TextBox>("LoginUserBox").Text = _settings.User;
                Get<TextBox>("LoginUserBox").IsEnabled = false;
                Get<TextBox>("PasswordBox").Text = "";
                Get<Grid>("LoginOverlay").IsVisible = true;
                ShowLoginStep(code: false, Str(m, "error") is { Length: > 0 } e1 ? e1
                    : Loc.Tr("Сохранённый вход истёк — Apple просит войти заново.", "The saved sign-in expired — Apple asks to sign in again."));
                SetStatus(MaterialIconKind.LockOutline, Loc.Tr("Ждёт вход в iCloud", "Waiting for iCloud sign-in"), "");
                break;

            case "need_code":
                _authTarget = _engine;
                Get<Grid>("LoginOverlay").IsVisible = true;
                ShowLoginStep(code: true, Str(m, "error"));
                SetStatus(MaterialIconKind.CellphoneKey, Loc.Tr("Ждёт код подтверждения", "Waiting for the verification code"), "");
                break;

            case "login_ok":
                if (Get<Grid>("LoginOverlay").IsVisible && _authTarget == _engine)
                {
                    Get<Grid>("LoginOverlay").IsVisible = false;
                    Log("ok", Loc.Tr("Вход выполнен, сессия сохранена.", "Signed in, the session is saved."));
                }
                _authTarget = null;
                if (Str(m, "name") is { Length: > 0 } name && name != _settings.Name)
                {
                    _settings.Name = name;
                    _settings.Save();
                }
                UpdateAccount();
                break;

            case "total":
                _total = Long(m, "total");
                Log("info", Loc.Tr($"В iCloud: {_total:N0} фото и видео.", $"In iCloud: {_total:N0} photos and videos."));
                UpdateStats();
                break;

            case "progress":
                _done = Long(m, "done");
                _existed = Long(m, "existed");
                _downloaded = Long(m, "downloaded");
                _failed = Long(m, "failed");
                _bytes = Long(m, "bytes");
                var signature = _done * 1_000_003 + _bytes;
                if (signature != _lastSignature)
                {
                    _lastSignature = signature;
                    _lastChange = DateTime.UtcNow;
                }
                _samples.Enqueue((DateTime.UtcNow, _bytes, _done));
                while (_samples.Count > 0 && DateTime.UtcNow - _samples.Peek().At > TimeSpan.FromSeconds(60)) _samples.Dequeue();
                UpdateActive(m);
                UpdateStats();
                break;

            case "file":
                Log("file", Loc.Tr($"Скачан {Relative(Str(m, "path"))}  ·  {Size(Long(m, "size"))}", $"Downloaded {Relative(Str(m, "path"))}  ·  {Size(Long(m, "size"))}"));
                break;

            case "log":
                Log(Str(m, "level"), Str(m, "message"), Str(m, "detail"));
                break;

            case "fatal":
                _fatalMessage = Str(m, "message");
                _fatalDetail = Str(m, "detail");
                _fatalNoRetry = m.TryGetProperty("retry", out var retry) && retry.ValueKind == JsonValueKind.False;
                _fatalAuth = m.TryGetProperty("auth", out var auth) && auth.ValueKind == JsonValueKind.True;
                Log("error", _fatalMessage, _fatalDetail);
                break;

            case "done":
                _gotDone = true;
                _total = Long(m, "total");
                _done = Long(m, "done");
                _existed = Long(m, "existed");
                _downloaded = Long(m, "downloaded");
                _failed = Long(m, "failed");
                if (Long(m, "missing") is > 0 and var missing)
                    Log("warning", Loc.Tr($"Фото без оригинала в iCloud (скачать нечего): {missing:N0}.", $"Photos with no original in iCloud (nothing to download): {missing:N0}."));
                if (m.TryGetProperty("failed_names", out var names))
                    foreach (var item in names.EnumerateArray())
                        Log("error", Loc.Tr($"Не скачалось: {item.GetString()}", $"Not downloaded: {item.GetString()}"));
                UpdateStats();
                break;
        }
    }

    void OnExited(int code, string tail)
    {
        _running = false;
        Platform.KeepAwake(false);
        if (_authTarget == _engine)
        {
            _authTarget = null;
            Get<Grid>("LoginOverlay").IsVisible = false;
        }
        _active.Clear();
        Get<TextBlock>("ActiveEmpty").IsVisible = true;
        UpdateButtons();
        UpdateAccount();

        if (_userStopped)
        {
            SetStatus(MaterialIconKind.PauseCircleOutline, Loc.Tr("Остановлено", "Stopped"),
                Loc.Tr("Нажмите «Докачать» — продолжу с того же места, уже скачанное не тронется.", "Press “Download” to continue from the same place; downloaded files stay as they are."));
            return;
        }

        if (_gotDone)
        {
            if (_failed == 0)
            {
                SetStatus(MaterialIconKind.CheckCircleOutline, Loc.Tr("Всё скачано", "Everything is downloaded"),
                    Loc.Tr($"В папке есть все {_total:N0} фото и видео из iCloud. Скачано в этот раз: {_downloaded:N0} ({Size(_bytes)}).",
                        $"The folder has all {_total:N0} photos and videos from iCloud. Downloaded this time: {_downloaded:N0} ({Size(_bytes)})."));
                Log("ok", Loc.Tr($"Готово: всё скачано. Новых файлов: {_downloaded:N0}, уже было: {_existed:N0}.", $"Done: everything is downloaded. New: {_downloaded:N0}, already there: {_existed:N0}."));
                _restarts = 0;
                return;
            }
            Log("error", Loc.Tr($"Готово, но не скачалось файлов: {_failed:N0}.", $"Done, but {_failed:N0} file(s) were not downloaded."));
            if (_settings.AutoRestart && _failedRounds < 3)
            {
                _failedRounds++;
                ScheduleRestart(60, Loc.Tr($"Не скачалось файлов: {_failed:N0}. Пробую ещё раз (проход {_failedRounds} из 3).", $"{_failed:N0} file(s) failed. Trying again (pass {_failedRounds} of 3)."));
            }
            else
            {
                ShowProblem(Loc.Tr($"Не скачалось файлов: {_failed:N0}, даже после повторов.", $"{_failed:N0} file(s) failed even after retries."),
                    Loc.Tr("Список — в журнале (галочка «только ошибки»).", "They are listed in the log (tick “errors only”)."), retry: true);
            }
            return;
        }

        var message = _fatalMessage
            ?? (_stallKill ? Loc.Tr("Загрузка зависла (4 минуты без прогресса) — перезапускаю.", "Downloading hung (no progress for 4 minutes) — restarting.") : null)
            ?? (code == -1 ? Loc.Tr("Движок не отвечал и был остановлен.", "The engine stopped responding and was stopped.")
                : Loc.Tr($"Движок неожиданно завершился (код {code}).", $"The engine ended unexpectedly (code {code})."));
        var detail = _fatalDetail is { Length: > 0 } ? _fatalDetail : tail;
        if (_fatalNoRetry)
        {
            if (_fatalAuth) UpdateAccount();
            ShowProblem(message, _fatalAuth ? "" : Short(detail), retry: true);
            SetStatus(MaterialIconKind.AlertCircleOutline, Loc.Tr("Нужно ваше участие", "Needs your attention"), message);
            return;
        }
        if (!string.IsNullOrWhiteSpace(tail) && _fatalMessage == null) Log("error", message, tail);
        if (_settings.AutoRestart)
        {
            _restarts++;
            ScheduleRestart(Math.Min(300, 15 * (1 << Math.Min(_restarts - 1, 4))), message);
        }
        else
        {
            ShowProblem(message, Short(detail), retry: true);
        }
    }

    void ScheduleRestart(int seconds, string reason)
    {
        _restartAt = DateTime.UtcNow.AddSeconds(seconds);
        ShowProblem(reason, Loc.Tr("Скачанное сохранено, продолжу с того же места.", "Downloaded files are kept; I'll continue from the same place."), retry: true);
        SetStatus(MaterialIconKind.Restart, Loc.Tr("Автоперезапуск…", "Restarting…"), reason);
        UpdateButtons();
    }

    // ------------------------------------------------------------------ every second

    void Tick()
    {
        if (_restartAt is { } at && !_engine.Running)
        {
            var left = (int)Math.Ceiling((at - DateTime.UtcNow).TotalSeconds);
            if (left <= 0)
            {
                _restartAt = null;
                Start(manual: false);
            }
            else
            {
                Get<TextBlock>("StatusDetail").Text = Loc.Tr($"Перезапуск через {left} с. Можно нажать «Повторить сейчас».", $"Restarting in {left} s. You can press “Retry now”.");
            }
        }

        // The old script's hang: downloads stop, nothing moves. Restart it.
        if (_running && _engine.Running && _total > 0 && !Get<Grid>("LoginOverlay").IsVisible
            && DateTime.UtcNow - _lastChange > Engine.Silence)
        {
            Log("error", Loc.Tr("Нет прогресса 4 минуты — похоже на зависание сети. Перезапускаю загрузку.", "No progress for 4 minutes — looks like a network hang. Restarting."));
            _stallKill = true;
            _lastChange = DateTime.UtcNow;
            _engine.Stop();
            _userStopped = false;
        }
    }

    // ------------------------------------------------------------------ view

    void SetReady()
    {
        if (_running) return;
        SetStatus(MaterialIconKind.CloudOutline, Loc.Tr("Готово к запуску", "Ready"),
            Loc.Tr("Нажмите «Докачать»: программа сверит iCloud с папкой и скачает только то, чего ещё нет.",
                "Press “Download”: the app compares iCloud with the folder and downloads only what is missing."));
    }

    void UpdateStats()
    {
        Get<TextBlock>("TotalText").Text = _total > 0 ? _total.ToString("N0") : "—";
        Get<TextBlock>("ExistedText").Text = _total > 0 ? _existed.ToString("N0") : "—";
        Get<TextBlock>("DownloadedText").Text = _total > 0 ? _downloaded.ToString("N0") : "—";
        Get<TextBlock>("LeftText").Text = _total > 0 ? Math.Max(0, _total - _done).ToString("N0") : "—";
        var failed = Get<TextBlock>("FailedText");
        failed.Text = _total > 0 ? _failed.ToString("N0") : "—";
        failed.Foreground = _failed > 0 ? Brush("Brush.Danger") : Brush("Brush.Text");

        var fraction = _total > 0 ? (double)_done / _total : 0;
        Get<SmoothBar>("Bar").Value = fraction;
        Get<TextBlock>("PercentText").Text = _total > 0 ? $"{fraction * 100:0.0}%" : "";

        double speed = 0, doneRate = 0;
        if (_samples.Count >= 2)
        {
            var first = _samples.Peek();
            var last = _samples.Last();
            var seconds = (last.At - first.At).TotalSeconds;
            if (seconds > 1)
            {
                var recent = _samples.Where(s => (last.At - s.At).TotalSeconds <= 10).ToList();
                var recentSeconds = (last.At - recent[0].At).TotalSeconds;
                speed = recentSeconds > 0.5 ? (last.Bytes - recent[0].Bytes) / recentSeconds : 0;
                doneRate = (last.Done - first.Done) / seconds;
            }
        }
        Get<TextBlock>("SpeedText").Text = _running ? $"{Size((long)speed)}/{Loc.Tr("с", "s")}" : "—";
        Get<TextBlock>("BytesText").Text = _bytes > 0 ? Loc.Tr($"скачано {Size(_bytes)}", $"{Size(_bytes)} downloaded") : Loc.Tr("скорость", "speed");
        var eta = Get<TextBlock>("EtaText");
        if (_running && doneRate > 0 && _total > _done)
            eta.Text = Loc.Tr($"осталось ≈ {Duration(TimeSpan.FromSeconds((_total - _done) / doneRate))}", $"≈ {Duration(TimeSpan.FromSeconds((_total - _done) / doneRate))} left");
        else
            eta.Text = _running ? Loc.Tr($"идёт {Duration(DateTime.UtcNow - _startedAt)}", $"running {Duration(DateTime.UtcNow - _startedAt)}") : "";
        Title = _running && _total > 0 ? $"{fraction * 100:0}% — iCloud Downloader" : "iCloud Downloader";
    }

    void UpdateActive(JsonElement m)
    {
        _active.Clear();
        if (m.TryGetProperty("active", out var active))
        {
            foreach (var file in active.EnumerateArray())
            {
                var done = Long(file, "done");
                var total = Long(file, "total");
                _active.Add(new ActiveItem
                {
                    Name = Relative(Str(file, "path")),
                    Info = total > 0 ? Loc.Tr($"{Size(done)} из {Size(total)}", $"{Size(done)} of {Size(total)}") : Size(done),
                    Fraction = total > 0 ? (double)done / total : 0,
                });
            }
        }
        Get<TextBlock>("ActiveEmpty").IsVisible = _active.Count == 0;
    }

    void SetStatus(MaterialIconKind icon, string title, string detail)
    {
        Get<MaterialIcon>("StageIcon").Kind = icon;
        Get<TextBlock>("StatusText").Text = title;
        Get<TextBlock>("StatusDetail").Text = detail;
    }

    void ShowProblem(string text, string detail, bool retry)
    {
        Get<Border>("ProblemBox").IsVisible = true;
        Get<TextBlock>("ProblemText").Text = text;
        Get<TextBlock>("ProblemDetail").Text = detail;
        Get<TextBlock>("ProblemDetail").IsVisible = detail.Length > 0;
        Get<Button>("RetryButton").IsVisible = retry;
    }

    void HideProblem() => Get<Border>("ProblemBox").IsVisible = false;

    void UpdateButtons()
    {
        Get<Button>("StartButton").IsVisible = !_running;
        Get<Button>("StopButton").IsVisible = _running || _restartAt != null;
        Get<TextBox>("FolderBox").IsEnabled = !_running;
        Get<Button>("BrowseButton").IsEnabled = !_running;
    }

    // ------------------------------------------------------------------ buttons

    async Task BrowseAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("main.folder") });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) Get<TextBox>("FolderBox").Text = path;
    }

    async Task CopyLogAsync()
    {
        var text = new StringBuilder();
        foreach (var item in (Get<CheckBox>("ErrorsOnlyBox").IsChecked == true ? _errors : _log))
            text.AppendLine($"{item.Time}  {item.Text}{(string.IsNullOrEmpty(item.Detail) ? "" : "  [" + item.Detail + "]")}");
        if (Clipboard != null) await Clipboard.SetTextAsync(text.ToString());
    }

    // ------------------------------------------------------------------ log

    void Log(string level, string text, string? detail = null)
    {
        var item = new LogItem
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Text = text,
            Detail = string.IsNullOrWhiteSpace(detail) || detail == text ? null : detail,
            Level = level,
        };
        _log.Add(item);
        if (_log.Count > 3000) _log.RemoveAt(0);
        if (item.IsError)
        {
            _errors.Add(item);
            if (_errors.Count > 3000) _errors.RemoveAt(0);
        }
        // Follow new lines only while the log is scrolled to the bottom; reading older lines is not interrupted.
        // ScrollIntoView on every line crashed Avalonia's virtualizing panel ("Invalid Arrange rectangle") when lines
        // came in fast, so the scroll to the end is deferred and done at most four times a second.
        var scroller = Get<ListBox>("LogList").FindDescendantOfType<ScrollViewer>();
        var atBottom = scroller == null || scroller.Offset.Y + scroller.Viewport.Height >= scroller.Extent.Height - 60;
        if (atBottom && !_scrollPending)
        {
            _scrollPending = true;
            DispatcherTimer.RunOnce(() =>
            {
                _scrollPending = false;
                Get<ListBox>("LogList").FindDescendantOfType<ScrollViewer>()?.ScrollToEnd();
            }, TimeSpan.FromMilliseconds(250), DispatcherPriority.Background);
        }
        try
        {
            File.AppendAllText(_logFile, $"{DateTime.Now:s} [{level}] {text}{(item.Detail != null ? "\n    " + item.Detail.Replace("\n", "\n    ") : "")}\n");
        }
        catch (IOException)
        {
        }
    }

    // ------------------------------------------------------------------ helpers

    static string Str(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static long Long(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    string Relative(string path)
    {
        var folder = _settings.Folder;
        if (folder.Length > 0 && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            return path[folder.Length..].TrimStart('\\', '/');
        return Path.GetFileName(path);
    }

    static string Short(string text) => text.Length > 600 ? "…" + text[^600..] : text;

    static string Size(long bytes)
    {
        string[] units = Loc.En ? ["B", "KB", "MB", "GB", "TB"] : ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    static string Duration(TimeSpan time)
    {
        if (time.TotalSeconds < 60) return Loc.Tr($"{(int)time.TotalSeconds} с", $"{(int)time.TotalSeconds} s");
        if (time.TotalMinutes < 60) return Loc.Tr($"{(int)time.TotalMinutes} мин", $"{(int)time.TotalMinutes} min");
        if (time.TotalHours < 48) return Loc.Tr($"{(int)time.TotalHours} ч {time.Minutes} мин", $"{(int)time.TotalHours} h {time.Minutes} min");
        return Loc.Tr($"{(int)time.TotalDays} д {time.Hours} ч", $"{(int)time.TotalDays} d {time.Hours} h");
    }
}
