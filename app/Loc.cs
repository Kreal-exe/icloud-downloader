using System.ComponentModel;
using Avalonia;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace iCloudDownloader;

/// <summary>Russian and English texts. XAML uses {l:T key}; code uses Loc.Tr("по-русски", "in English").</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static readonly Loc Instance = new();

    public static string Language { get; private set; } = "ru";
    public static bool En => Language == "en";

    public event PropertyChangedEventHandler? PropertyChanged;

    public static void Set(string language)
    {
        Language = language == "en" ? "en" : "ru";
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
    }

    public static string Tr(string ru, string en) => En ? en : ru;

    public static string T(string key) => Texts.TryGetValue(key, out var t) ? (En ? t.En : t.Ru) : key;

    public string this[string key] => T(key);

    static readonly Dictionary<string, (string Ru, string En)> Texts = new()
    {
        ["header.openFolder"] = ("Открыть папку", "Open folder"),
        ["header.settings"] = ("Настройки", "Settings"),
        ["header.theme"] = ("Светлая / тёмная тема", "Light / dark theme"),
        ["account.signIn"] = ("Войти в iCloud", "Sign in to iCloud"),
        ["account.signOut"] = ("Выйти", "Sign out"),
        ["account.connected"] = ("подключено", "connected"),
        ["account.none"] = ("Вход не выполнен", "Not signed in"),

        ["main.folder"] = ("КУДА СКАЧИВАТЬ", "DOWNLOAD TO"),
        ["main.threads"] = ("ПОТОКОВ", "PARALLEL"),
        ["main.threadsTip"] = ("Сколько файлов качать одновременно. Можно менять прямо во время загрузки. Обычно лучше всего 6–8; если появляются ошибки «Apple ограничил запросы» — уменьшите.",
            "How many files to download at once. Can be changed while downloading. 6–8 is usually fastest; if Apple starts throttling, use fewer."),
        ["main.start"] = ("Докачать", "Download"),
        ["main.stop"] = ("Стоп", "Stop"),

        ["stat.total"] = ("всего в iCloud", "in iCloud"),
        ["stat.existed"] = ("уже было в папке", "already in folder"),
        ["stat.downloaded"] = ("скачано сейчас", "downloaded now"),
        ["stat.left"] = ("осталось проверить", "left to check"),
        ["stat.failed"] = ("ошибок", "errors"),

        ["active.title"] = ("Сейчас качается", "Downloading now"),
        ["active.empty"] = ("Пока ничего не качается.", "Nothing is downloading."),
        ["log.title"] = ("Журнал", "Log"),
        ["log.errorsOnly"] = ("только ошибки", "errors only"),
        ["log.copy"] = ("Копировать", "Copy"),
        ["problem.retry"] = ("Повторить сейчас", "Retry now"),

        ["login.title"] = ("Вход в iCloud", "Sign in to iCloud"),
        ["login.appleId"] = ("APPLE ID", "APPLE ID"),
        ["login.password"] = ("ПАРОЛЬ", "PASSWORD"),
        ["login.passwordWatermark"] = ("Пароль Apple ID", "Apple ID password"),
        ["login.remember"] = ("Запомнить пароль на этом компьютере", "Remember the password on this computer"),
        ["login.hint"] = ("Пароль уходит только в Apple. После входа и кода подтверждения сессия сохраняется, и следующий раз входить не нужно.",
            "The password goes only to Apple. After signing in with the verification code the session is saved, so you won't have to sign in next time."),
        ["login.cancel"] = ("Отмена", "Cancel"),
        ["login.submit"] = ("Войти", "Sign in"),
        ["code.title"] = ("Код подтверждения", "Verification code"),
        ["code.hint"] = ("Apple отправил 6-значный код на ваши устройства — iPhone, iPad или Mac. Введите его ниже.",
            "Apple sent a 6-digit code to your devices — iPhone, iPad or Mac. Enter it below."),
        ["code.noCode"] = ("Код не пришёл? На iPhone: Настройки → [ваше имя] → Вход и безопасность → Получить код.",
            "No code? On your iPhone: Settings → [your name] → Sign-In & Security → Get Verification Code."),
        ["code.submit"] = ("Подтвердить", "Verify"),
        ["busy.checking"] = ("Проверяю…", "Checking…"),

        ["settings.title"] = ("Настройки", "Settings"),
        ["settings.language"] = ("Язык", "Language"),
        ["settings.theme"] = ("Тема", "Theme"),
        ["theme.system"] = ("Как в системе", "System"),
        ["theme.light"] = ("Светлая", "Light"),
        ["theme.dark"] = ("Тёмная", "Dark"),
        ["settings.autoRestart"] = ("Перезапускать загрузку при обрыве или зависании", "Restart downloading after a network drop or a hang"),
        ["settings.hidden"] = ("Скачивать скрытые фото", "Download hidden photos"),
        ["settings.hiddenHint"] = ("Альбом «Скрытые» не входит во «Все фото». Если включить, скрытые фото скачиваются в отдельную папку Hidden внутри папки загрузки.",
            "The Hidden album is not part of All Photos. When on, hidden photos are downloaded into a separate Hidden folder inside the download folder."),
        ["settings.videosByName"] =("Не перекачивать сжатые видео", "Don't re-download compressed videos"),
        ["settings.videosByNameHint"] = ("Видео, которое уже есть в папке под тем же именем, считается скачанным, даже если его размер другой (например, его сжали в Photo Organizer). Фото по-прежнему сверяются по имени и размеру.",
            "A video already in the folder under the same name counts as downloaded even if its size differs (e.g. compressed with Photo Organizer). Photos are still matched by name and size."),
        ["settings.keepAwake"] =("Не давать компьютеру засыпать во время загрузки", "Keep the computer awake while downloading"),
        ["settings.threadsTitle"] = ("Сколько потоков ставить", "How many parallel downloads"),
        ["settings.threadsText"] = ("Каждое соединение с iCloud даёт примерно 3–8 МБ/с, поэтому больше потоков — быстрее, пока не упрётесь в скорость интернета. Обычно лучше всего 6–8. Больше 10–12 Apple начинает ограничивать запросы. Число можно менять прямо во время загрузки и смотреть на скорость.",
            "Each connection to iCloud gives about 3–8 MB/s, so more parallel downloads are faster until your internet is the limit. 6–8 is usually best; above 10–12 Apple starts throttling. You can change it while downloading and watch the speed."),
        ["settings.logs"] = ("Папка журналов", "Logs folder"),
        ["settings.close"] = ("Готово", "Done"),
    };
}

/// <summary>{l:T key}: a text that follows the language.</summary>
public sealed class T : MarkupExtension
{
    public T(string key) => Key = key;

    public string Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) => ((IObservable<string>)new LocText(Key)).ToBinding();
}

/// <summary>The text of one key now, and again after every language change.</summary>
sealed class LocText(string key) : IObservable<string>
{
    public IDisposable Subscribe(IObserver<string> observer)
    {
        observer.OnNext(Loc.T(key));
        PropertyChangedEventHandler handler = (_, _) => observer.OnNext(Loc.T(key));
        Loc.Instance.PropertyChanged += handler;
        return new Unsubscriber(() => Loc.Instance.PropertyChanged -= handler);
    }

    sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
