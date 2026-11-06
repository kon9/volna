using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volna.Core;

namespace Volna.App;

// A window's life ends with Closing, which releases these, rather than with a Dispose nobody would call.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Released when the window closes.")]
public partial class MainWindow : Window
{
    static readonly int[] Heights = { 0, 2160, 1440, 1080, 720, 480, 360 };
    static readonly string[] AudioFormats = { "mp3", "m4a", "best" };
    static readonly string[] Themes = { "dark", "light", "system" };
    static readonly string[] Languages = { "auto", "en", "ru" };
    const string SignInPage = "https://accounts.google.com/ServiceLogin?service=youtube&uilel=3&passive=true&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26next%3D%252F";

    readonly IQueueBackend backend;
    readonly Preferences prefs;
    public QueueController Queue { get; }
    readonly ObservableCollection<QueueItem> visible = new();
    readonly ObservableCollection<QueueItem> candidates = new();
    readonly DispatcherTimer toastTimer = new(), clipboardTimer = new() { Interval = TimeSpan.FromSeconds(1.5) }, followTimer = new() { Interval = TimeSpan.FromHours(1) };
    readonly DispatcherTimer logFlush = new() { Interval = TimeSpan.FromMilliseconds(300) }, queueSaver = new() { Interval = TimeSpan.FromSeconds(4) }, filterTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly StringBuilder logPending = new();
    StreamWriter? logWriter;
    int logLength;
    string lastClipboard = "";
    bool updating, reallyClose, loading = true, pickerTabSwitching;
    int filterSignature;
    QueueItem? pickerTarget, clipTarget;
    ImportTable? importTable;
    DateTime clearArmedUntil;
    ProcessRunner? pickerRunner;
    CancellationTokenSource? setupCancel, signInCancel;
    List<BrowserInfo> browsers = new();
    readonly bool checkTools;
    readonly Taskbar taskbar = new();

    public MainWindow() : this(new YtDlpBackend(), new Preferences(), new List<string>(), false) { }

    public MainWindow(IQueueBackend backend, Preferences prefs, List<string> startupEntries, bool checkTools)
    {
        this.backend = backend;
        this.prefs = prefs;
        this.checkTools = checkTools;
        InitializeComponent();
        Queue = new QueueController(backend, prefs);
        Queue.Toast += ShowToast;
        Queue.Log += Log;
        Queue.SummaryChanged += UpdateSummary;
        Queue.RunCompleted += OnRunCompleted;
        Queue.Items.CollectionChanged += (_, _) => UpdateFilterSoon();
        QueueList.ItemsSource = Queue.Items;
        PickerList.ItemsSource = candidates;
        FollowedItems.ItemsSource = Queue.Followed;
        Queue.Followed.CollectionChanged += (_, _) => FollowedPanel.IsVisible = Queue.Followed.Count > 0;
        FollowedPanel.IsVisible = Queue.Followed.Count > 0;
        LoadPreferences();
        Wire();
        UpdateAccount();
        UpdateSummary();
        ApplyDecorations();
        loading = false;
        Opened += async (_, _) =>
        {
            FitScreen();
            InputBox.Focus();
            logFlush.Start();
            if (checkTools)
            {
                await EnsureToolsAsync();
                // Twice in a row points at something outside the app, which the person should hear about.
                if (await ToolManager.RemoveBrokenAsync())
                {
                    await EnsureToolsAsync();
                    if (await ToolManager.RemoveBrokenAsync())
                    {
                        ShowToast(Loc.T("toast.tools.blocked"), ToastKind.Error);
                        await EnsureToolsAsync();
                    }
                }
                _ = LoadVersionAsync();
                _ = CheckForNewVersionAsync();
                if (ToolManager.UpdateDue()) _ = UpdateYtDlpAsync(true);
            }
            int restored = checkTools ? Queue.RestoreQueue() : 0;
            if (restored > 0) ShowToast(Loc.T("toast.restored", restored), ToastKind.Info);
            if (checkTools)
            {
                queueSaver.Start();
                clipboardTimer.Start();
                Queue.CheckFollowed(false);
                followTimer.Start();
            }
            if (startupEntries.Count > 0) AddArguments(string.Join("\n", startupEntries));
        };
    }

    // ---------- Setup ----------

    // What fits in one row of a wide window takes two, and icons alone, on a narrow one.
    void FitQueueHeader(double width)
    {
        bool narrow = width < 1120;
        QueueActions.Classes.Set("compact", narrow);
        Grid.SetRow(FilterBox, narrow ? 1 : 0);
        Grid.SetColumn(FilterBox, narrow ? 0 : 2);
        Grid.SetColumnSpan(FilterBox, narrow ? 3 : 1);
        FilterBox.Margin = new Thickness(0, narrow ? 8 : 0, 0, 0);
    }

    // A saved or default size can be larger than this screen, or than a small laptop at a high scale.
    void FitScreen()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen) return;
        double width = screen.WorkingArea.Width / screen.Scaling, height = screen.WorkingArea.Height / screen.Scaling;
        if (Width > width) Width = Math.Max(MinWidth, width);
        if (Height > height) Height = Math.Max(MinHeight, height);
    }

    void ApplyDecorations()
    {
        // Windows and macOS let the app draw into the title bar. Linux desktops differ too much to rely on that.
        if (OperatingSystem.IsLinux()) return;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = -1;
        TitleBar.Margin = new Thickness(OperatingSystem.IsMacOS() ? 84 : 20, 10, 20 + (OperatingSystem.IsWindows() ? 140 : 0), 8);
    }

    void LoadPreferences()
    {
        if (string.IsNullOrWhiteSpace(prefs.Folder)) prefs.Folder = AppPaths.DefaultDownloads;
        FolderBox.Text = prefs.Folder;
        int q = Array.IndexOf(Heights, prefs.Height);
        QualityBox.SelectedIndex = q < 0 ? 3 : q;
        ContainerBox.SelectedIndex = prefs.Container == "mkv" ? 1 : 0;
        int a = Array.IndexOf(AudioFormats, prefs.AudioFormat);
        AudioFormatBox.SelectedIndex = a < 0 ? 0 : a;
        ModeAudio.IsChecked = prefs.Audio;
        ModeVideo.IsChecked = !prefs.Audio;
        ShowModeOptions();
        new[] { Par1, Par2, Par3, Par4 }[Math.Clamp(prefs.Parallel, 1, 4) - 1].IsChecked = true;
        EmbedThumbBox.IsChecked = prefs.EmbedThumbnail;
        SubsBox.IsChecked = prefs.Subtitles;
        SponsorBox.IsChecked = prefs.SponsorBlock;
        OpenAfterBox.IsChecked = prefs.OpenFolder;
        SoundBox.IsChecked = prefs.Sound;
        AutoStartBox.IsChecked = prefs.AutoStart;
        ClipboardBox.IsChecked = prefs.WatchClipboard;
        OriginalBox.IsChecked = prefs.OriginalTracks;
        LyricsBox.IsChecked = prefs.Lyrics;
        PlaylistsBox.IsChecked = prefs.WritePlaylists;
        SkipDoneBox.IsChecked = prefs.SkipDownloaded;
        CleanNamesBox.IsChecked = prefs.CleanNames;
        ThemeBox.SelectedIndex = Math.Max(0, Array.IndexOf(Themes, prefs.Theme));
        LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(Languages, prefs.Language));
        ApplyTheme();
        if (prefs.WindowWidth >= MinWidth && prefs.WindowHeight >= MinHeight) { Width = prefs.WindowWidth; Height = prefs.WindowHeight; }
        if (prefs.Maximized) WindowState = WindowState.Maximized;
    }

    void ApplyTheme()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = prefs.Theme switch { "light" => ThemeVariant.Light, "system" => ThemeVariant.Default, _ => ThemeVariant.Dark };
    }

    void ShowModeOptions()
    {
        bool audio = ModeAudio.IsChecked == true;
        VideoOptions.IsVisible = !audio;
        AudioOptions.IsVisible = audio;
    }

    void Wire()
    {
        AddHandler(Button.ClickEvent, OnClick);
        MenuItem.ClickEvent.AddClassHandler<MenuItem>((item, e) => OnClick(item, e));

        void Changed(Action update) { if (!loading) update(); }
        ModeVideo.IsCheckedChanged += (_, _) => Changed(OnModeChanged);
        ModeAudio.IsCheckedChanged += (_, _) => Changed(OnModeChanged);
        QualityBox.SelectionChanged += (_, _) => Changed(() => { prefs.Height = Heights[Math.Max(0, QualityBox.SelectedIndex)]; Queue.RefreshLabels(); });
        ContainerBox.SelectionChanged += (_, _) => Changed(() => { prefs.Container = ContainerBox.SelectedIndex == 1 ? "mkv" : "mp4"; Queue.RefreshLabels(); });
        AudioFormatBox.SelectionChanged += (_, _) => Changed(() => { prefs.AudioFormat = AudioFormats[Math.Max(0, AudioFormatBox.SelectedIndex)]; Queue.RefreshLabels(); });
        var pars = new[] { Par1, Par2, Par3, Par4 };
        for (int n = 0; n < pars.Length; n++)
        {
            int value = n + 1;
            pars[n].IsCheckedChanged += (s, _) => Changed(() => { if (((RadioButton)s!).IsChecked == true) { prefs.Parallel = value; Queue.Pump(); } });
        }
        EmbedThumbBox.IsCheckedChanged += (_, _) => prefs.EmbedThumbnail = EmbedThumbBox.IsChecked == true;
        SubsBox.IsCheckedChanged += (_, _) => prefs.Subtitles = SubsBox.IsChecked == true;
        SponsorBox.IsCheckedChanged += (_, _) => prefs.SponsorBlock = SponsorBox.IsChecked == true;
        OpenAfterBox.IsCheckedChanged += (_, _) => prefs.OpenFolder = OpenAfterBox.IsChecked == true;
        LyricsBox.IsCheckedChanged += (_, _) => prefs.Lyrics = LyricsBox.IsChecked == true;
        CleanNamesBox.IsCheckedChanged += (_, _) => Changed(() => { prefs.CleanNames = CleanNamesBox.IsChecked == true; Queue.RefreshLabels(); });
        PlaylistsBox.IsCheckedChanged += (_, _) => prefs.WritePlaylists = PlaylistsBox.IsChecked == true;
        SkipDoneBox.IsCheckedChanged += (_, _) => Changed(() => { prefs.SkipDownloaded = SkipDoneBox.IsChecked == true; Queue.RefreshLabels(); });
        SoundBox.IsCheckedChanged += (_, _) => prefs.Sound = SoundBox.IsChecked == true;
        AutoStartBox.IsCheckedChanged += (_, _) => prefs.AutoStart = AutoStartBox.IsChecked == true;
        ClipboardBox.IsCheckedChanged += (_, _) => Changed(() =>
        {
            prefs.WatchClipboard = ClipboardBox.IsChecked == true;
            if (prefs.WatchClipboard) ShowToast(Loc.T("toast.clipboard.on"), ToastKind.Info);
        });
        OriginalBox.IsCheckedChanged += (_, _) => Changed(() => { prefs.OriginalTracks = OriginalBox.IsChecked == true; Queue.ApplySources(); });
        ThemeBox.SelectionChanged += (_, _) => Changed(() => { prefs.Theme = Themes[Math.Max(0, ThemeBox.SelectedIndex)]; ApplyTheme(); });
        LanguageBox.SelectionChanged += (_, _) => Changed(() => { prefs.Language = Languages[Math.Max(0, LanguageBox.SelectedIndex)]; LanguageNote.IsVisible = true; });
        ProxyBox.Text = prefs.Proxy;
        SetupProxy.Text = prefs.Proxy;
        ProxyBox.TextChanged += (_, _) => prefs.Proxy = (ProxyBox.Text ?? "").Trim();
        FolderBox.LostFocus += (_, _) => { prefs.Folder = (FolderBox.Text ?? "").Trim(); Queue.RefreshLabels(); };

        InputBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0) { e.Handled = true; AddFromInput(); }
        }, RoutingStrategies.Tunnel);
        InputBox.AddHandler(TextBox.PastingFromClipboardEvent, (_, e) => { e.Handled = true; _ = PasteIntoInputAsync(); }, RoutingStrategies.Bubble);
        InputBox.GotFocus += (_, _) => InputCard.BorderBrush = new SolidColorBrush(Color.Parse("#FF4D67"));
        InputBox.LostFocus += (_, _) => InputCard[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Line");
        AddHandler(KeyDownEvent, OnWindowKey, RoutingStrategies.Tunnel);
        PickerQuery.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = PickerSearchAsync(); } }, RoutingStrategies.Tunnel);
        PickerMusic.IsCheckedChanged += (_, _) => OnPickerTabChanged(PickerMusic);
        PickerVideos.IsCheckedChanged += (_, _) => OnPickerTabChanged(PickerVideos);
        ImportArtist.SelectionChanged += (_, _) => Changed(RefreshImport);
        ImportTitle.SelectionChanged += (_, _) => Changed(RefreshImport);
        ImportHeader.IsCheckedChanged += (_, _) => Changed(RefreshImport);

        foreach (var filter in new[] { FilterAll, FilterWaiting, FilterActive, FilterDone, FilterFailed, FilterCheck })
            filter.IsCheckedChanged += (s, _) => { if (((RadioButton)s!).IsChecked == true) ApplyFilter(); };
        FilterBox.TextChanged += (_, _) => ApplyFilter();
        filterTimer.Tick += (_, _) => RefreshFilter();

        DragDrop.SetAllowDrop(Root, true);
        Root.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        Root.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = DragDropEffects.Copy; e.Handled = true; });
        Root.AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropOverlay.IsVisible = false);
        Root.AddHandler(DragDrop.DropEvent, OnDrop);

        toastTimer.Tick += (_, _) => { toastTimer.Stop(); Toast.Opacity = 0; };
        logFlush.Tick += (_, _) => FlushLog();
        queueSaver.Tick += (_, _) => Queue.SaveQueue(false);
        followTimer.Tick += (_, _) => Queue.CheckFollowed(false);
        clipboardTimer.Tick += async (_, _) => await CheckClipboardAsync();
        Program.EntriesFromOtherInstance += text =>
        {
            Activate();
            AddArguments(text);
        };
        Closing += OnClosing;
        SizeChanged += (_, e) => FitQueueHeader(e.NewSize.Width);
        FitQueueHeader(Width);
    }

    void OnModeChanged()
    {
        ShowModeOptions();
        prefs.Audio = ModeAudio.IsChecked == true;
        Queue.ModeChanged(prefs.Audio);
    }

    // ---------- Commands ----------

    void OnClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Control || sender == null) return;
        Control? element = sender as MenuItem;
        element ??= (e.Source as Control)?.FindAncestorOfType<Button>(true);
        string? command = element?.Tag as string;
        if (command == null) return;
        e.Handled = true;
        try
        {
            if (command == "unfollow" && element!.DataContext is FollowedList followed) Queue.Unfollow(followed);
            else if (command == "modeMenu") element!.ContextMenu?.Open(element);
            // The same menu a right-click opens, which many people never find.
            else if (command == "cardMenu") OpenCardMenu(element!);
            else Execute(command, element!.DataContext as QueueItem);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowToast(ex.Message, ToastKind.Error); }
    }

    void Execute(string command, QueueItem? item)
    {
        switch (command)
        {
            case "add": AddFromInput(); return;
            case "paste": _ = AddFromClipboardAsync(); return;
            case "import": _ = ImportFilesAsync(); return;
            case "checkFollowed": Queue.CheckFollowed(true); return;
            case "openRelease": TryOpen("https://github.com/" + AppUpdate.Repository + "/releases/latest"); return;
            case "clipSet": ApplyClip(ClipFrom.Text, ClipTo.Text); return;
            case "clipClear": ApplyClip(null, null); return;
            case "clipClose": ClipOverlay.IsVisible = false; return;
            case "start": prefs.Folder = (FolderBox.Text ?? "").Trim(); Queue.StartAll(); return;
            case "stop": Queue.StopAll(); return;
            case "update": _ = UpdateYtDlpAsync(false); return;
            case "toggleLog": ToggleLog(); return;
            case "settings": SettingsOverlay.IsVisible = true; return;
            case "settingsClose": SettingsOverlay.IsVisible = false; return;
            case "settingsLog": SettingsOverlay.IsVisible = false; ToggleLog(); return;
            case "importAdd": AddImport(); return;
            case "importPlain": AddImportAsList(); return;
            case "importClose": ImportOverlay.IsVisible = false; importTable = null; return;
            case "copyFailed": _ = CopyTextAsync(Queue.FailedList(), "toast.copied.failed"); return;
            case "copyLog": FlushLog(); _ = CopyTextAsync(LogBox.Text, "toast.copied.log"); return;
            case "openLogs": TryOpen(AppPaths.Logs); return;
            case "browse": _ = BrowseFolderAsync(); return;
            case "openFolder":
                Directory.CreateDirectory(prefs.Folder);
                TryOpen(prefs.Folder);
                return;
            case "account":
                OpenAuth();
                return;
            case "signin": OpenAuth(); return;
            case "signout": SignOut(); return;
            case "authUse": _ = UseBrowserAsync(); return;
            case "authOpenPage": TryOpen(SignInPage); return;
            case "authSeparate": _ = SeparateSignInAsync(); return;
            case "authImport": _ = ImportCookiesAsync(); return;
            case "authClose": CloseAuth(); return;
            case "pickerSearch": _ = PickerSearchAsync(); return;
            case "pickerClose": ClosePicker(); return;
            case "setupRetry":
                prefs.Proxy = (SetupProxy.Text ?? "").Trim();
                ProxyBox.Text = prefs.Proxy;
                _ = EnsureToolsAsync();
                return;
            case "setupSkip": SetupOverlay.IsVisible = false; return;
            case "closeYes": reallyClose = true; Close(); return;
            case "closeNo": CloseOverlay.IsVisible = false; return;
            case "clearDone": Queue.ClearDone(); return;
            case "clearAll":
                // A long list took a while to build, and one stray click must not cost it.
                if (Queue.Items.Count >= 20 && DateTime.Now > clearArmedUntil)
                {
                    clearArmedUntil = DateTime.Now.AddSeconds(4);
                    ShowToast(Loc.T("toast.clear.confirm"), ToastKind.Warning);
                    return;
                }
                clearArmedUntil = default;
                Queue.ClearAll();
                return;
            case "retryFailed": Queue.RetryFailed(); return;
            case "allVideo": Queue.SetAllModes(false); return;
            case "allAudio": Queue.SetAllModes(true); return;
        }
        if (item == null) return;
        if (command.StartsWith("quality:", StringComparison.Ordinal))
        {
            int height = int.Parse(command[8..], CultureInfo.InvariantCulture);
            foreach (var card in Targets(item)) Queue.SetItemQuality(card, height);
            return;
        }
        switch (command)
        {
            case "modeAudio":
                foreach (var card in Targets(item)) Queue.SetItemMode(card, true);
                return;
            case "pick":
                if (pickerTarget != null && Queue.Pick(pickerTarget, item)) ClosePicker();
                break;
            case "open":
                if (item.FilePath != null && File.Exists(item.FilePath)) TryOpen(item.FilePath);
                else ShowToast(Loc.T("toast.openfailed", item.FilePath ?? item.Title), ToastKind.Warning);
                break;
            case "reveal":
                if (item.FilePath != null && File.Exists(item.FilePath)) Desktop.Reveal(item.FilePath);
                else ShowToast(Loc.T("toast.openfailed", item.FilePath ?? item.Title), ToastKind.Warning);
                break;
            case "startOne":
                prefs.Folder = (FolderBox.Text ?? "").Trim();
                Queue.RetryMany(Targets(item).Where(c => c.ShowStartOne).ToList());
                break;
            case "retry":
                prefs.Folder = (FolderBox.Text ?? "").Trim();
                Queue.RetryMany(Targets(item).Where(c => c.ShowRetry).ToList());
                break;
            case "cancel":
                foreach (var card in Targets(item)) Queue.Cancel(card);
                break;
            case "remove":
                foreach (var card in Targets(item)) Queue.Remove(card);
                break;
            case "pickVersion": OpenPicker(item); break;
            case "acceptMatch":
                foreach (var card in Targets(item).Where(c => c.ShowReview)) Queue.AcceptMatch(card);
                break;
            case "follow": Queue.Follow(item); break;
            case "downloadAgain":
                prefs.Folder = (FolderBox.Text ?? "").Trim();
                foreach (var card in Targets(item).Where(c => c.CanDownloadAgain)) Queue.DownloadAgain(card);
                break;
            case "expandPlaylist": Queue.ExpandPlaylist(item); break;
            case "clip": OpenClip(item); break;
            case "browser":
                string? page = item.WebpageUrl ?? (item.IsSearch ? null : item.SourceUrl);
                if (page != null) TryOpen(page);
                break;
            case "copyLink": _ = CopyTextAsync(item.WebpageUrl ?? item.SourceUrl, "toast.copied.link"); break;
            case "copyItemLog": _ = CopyTextAsync(string.Join(Environment.NewLine, item.Log), "toast.copied.log"); break;
        }
    }

    // A menu opens only from the control it belongs to, so it is shown from the card and placed by the button.
    static void OpenCardMenu(Control button)
    {
        if (button.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.ContextMenu != null) is not { ContextMenu: { } menu } card) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.BottomEdgeAlignedRight;
        void Reset(object? sender, RoutedEventArgs e)
        {
            menu.Closed -= Reset;
            menu.PlacementTarget = null;
            menu.Placement = PlacementMode.Pointer;
        }
        menu.Closed += Reset;
        menu.Open(card);
    }

    // An action on a selected card applies to everything selected; on any other card, to that one.
    List<QueueItem> Targets(QueueItem item)
    {
        var selected = QueueList.SelectedItems?.OfType<QueueItem>().ToList() ?? new();
        return selected.Contains(item) ? selected : new List<QueueItem> { item };
    }

    void OnWindowKey(object? sender, KeyEventArgs e)
    {
        bool command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (e.Key == Key.Escape)
        {
            if (PickerOverlay.IsVisible) { ClosePicker(); e.Handled = true; }
            else if (AuthOverlay.IsVisible) { CloseAuth(); e.Handled = true; }
            else if (CloseOverlay.IsVisible) { CloseOverlay.IsVisible = false; e.Handled = true; }
            else if (ClipOverlay.IsVisible) { ClipOverlay.IsVisible = false; e.Handled = true; }
            else if (SettingsOverlay.IsVisible) { SettingsOverlay.IsVisible = false; e.Handled = true; }
            else if (ImportOverlay.IsVisible) { ImportOverlay.IsVisible = false; importTable = null; e.Handled = true; }
            else if (LogPanel.IsVisible) { ToggleLog(); e.Handled = true; }
            return;
        }
        if (OverlayOpen) return;
        bool typing = FocusManager?.GetFocusedElement() is TextBox;
        if (command && e.Key == Key.Enter)
        {
            e.Handled = true;
            Execute("start", null);
            return;
        }
        if (command && e.Key == Key.V && !typing) { e.Handled = true; _ = AddFromClipboardAsync(); }
        else if (command && e.Key == Key.A && !typing) { e.Handled = true; QueueList.SelectAll(); }
        else if (e.Key == Key.Delete && !typing && QueueList.SelectedItems is { Count: > 0 } selected)
        {
            e.Handled = true;
            foreach (var card in selected.OfType<QueueItem>().ToList()) Queue.Remove(card);
        }
        else if (command && e.Key == Key.L) { e.Handled = true; ToggleLog(); }
    }

    void ToggleLog()
    {
        FlushLog();
        LogPanel.IsVisible = !LogPanel.IsVisible;
        if (LogPanel.IsVisible) LogBox.CaretIndex = int.MaxValue;
    }

    async Task<string?> ReadClipboardAsync()
    {
        IClipboard? clipboard = Clipboard;
        return clipboard == null ? null : await clipboard.TryGetTextAsync();
    }

    async Task CopyTextAsync(string? text, string messageKey)
    {
        if (string.IsNullOrEmpty(text)) { ShowToast(Loc.T("toast.nothingtocopy"), ToastKind.Info); return; }
        try
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
            lastClipboard = text; // what this app copied must not come back as "a link from the clipboard"
            ShowToast(Loc.T(messageKey), ToastKind.Success);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowToast(Loc.T("toast.clipboardbusy"), ToastKind.Warning); }
    }

    void TryOpen(string target)
    {
        try { Desktop.Open(target); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { ShowToast(Loc.T("toast.openfailed", ex.Message), ToastKind.Error); }
    }

    async Task BrowseFolderAsync()
    {
        IStorageFolder? start = Directory.Exists(prefs.Folder) ? await StorageProvider.TryGetFolderFromPathAsync(prefs.Folder) : null;
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("settings.browse"), SuggestedStartLocation = start, AllowMultiple = false });
        string? path = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        if (path == null) return;
        FolderBox.Text = path;
        prefs.Folder = path;
        prefs.Save();
        Queue.RefreshLabels();
    }

    // ---------- Adding ----------

    void AddEntries(IList<string> entries, string source)
    {
        var (added, skipped) = Queue.Add(entries);
        if (added == 0 && skipped > 0) ShowToast(Loc.T(skipped == 1 ? "toast.already" : "toast.already.many"), ToastKind.Info);
        else if (source == "clipboard") ShowToast(added == 1 ? Loc.T("toast.clipboard") : Loc.T("toast.clipboard.many", added), ToastKind.Success);
        else if (added > 1)
        {
            int searches = entries.Count(Engine.IsSearch);
            string text = searches > 0 ? Loc.T("toast.added.search", added, searches) : Loc.T("toast.added", added);
            if (skipped > 0) text += Loc.T("toast.added.skipped", skipped);
            ShowToast(text, ToastKind.Success);
        }
        if (added > 0 && Queue.Items.Count > 0 && ReferenceEquals(QueueList.ItemsSource, Queue.Items)) QueueList.ScrollIntoView(Queue.Items.Count - 1);
    }

    void AddFromInput()
    {
        List<string> entries = Engine.ParseEntries(InputBox.Text);
        if (entries.Count == 0) { ShowToast(Loc.T("toast.typesomething"), ToastKind.Info); InputBox.Focus(); return; }
        InputBox.Text = "";
        AddEntries(entries, "input");
    }

    // The contents of an exported CSV can arrive by the clipboard as well as in a file.
    bool AddIfLibrary(string? text, string listName = "")
    {
        List<LibraryTrack> tracks = LibraryImport.Parse(text, listName);
        if (tracks.Count > 0)
        {
            lastClipboard = text ?? "";
            AddTracks(tracks);
            return true;
        }
        if (LibraryImport.GuessTable(text, listName) is not { } table) return false;
        lastClipboard = text ?? "";
        OpenImport(table);
        return true;
    }

    // A table of unknown origin: the person sees how it was understood before anything is searched.
    public void OpenImport(ImportTable table)
    {
        loading = true;
        importTable = table;
        var columns = Enumerable.Range(0, table.Columns).Select(n => Loc.T("import.column", n + 1, table.Sample(n))).ToList();
        ImportTitle.ItemsSource = columns;
        ImportTitle.SelectedIndex = table.TitleColumn;
        ImportArtist.ItemsSource = columns.Prepend(Loc.T("import.none")).ToList();
        ImportArtist.SelectedIndex = table.ArtistColumn + 1;
        ImportHeader.IsChecked = table.FirstRowIsHeader;
        loading = false;
        RefreshImport();
        ImportOverlay.IsVisible = true;
    }

    void RefreshImport()
    {
        if (importTable == null) return;
        importTable.TitleColumn = Math.Max(0, ImportTitle.SelectedIndex);
        importTable.ArtistColumn = ImportArtist.SelectedIndex - 1;
        importTable.FirstRowIsHeader = ImportHeader.IsChecked == true;
        var tracks = importTable.Tracks();
        ImportPreview.ItemsSource = tracks.Take(5).Select(t => t.Artist.Length > 0 ? t.Artist + " - " + t.Title : t.Title).ToList();
        ImportAddText.Text = Loc.T("import.add", tracks.Count);
    }

    void AddImport()
    {
        if (importTable == null) return;
        AddTracks(importTable.Tracks());
        ImportOverlay.IsVisible = false;
        importTable = null;
    }

    void AddImportAsList()
    {
        if (importTable == null) return;
        var entries = Engine.ParseEntries(importTable.Text);
        ImportOverlay.IsVisible = false;
        importTable = null;
        if (entries.Count > 0) AddEntries(entries, "paste");
    }

    void AddTracks(List<LibraryTrack> tracks)
    {
        var (added, skipped) = Queue.AddTracks(tracks);
        if (added == 0 && skipped > 0) ShowToast(Loc.T(skipped == 1 ? "toast.already" : "toast.already.many"), ToastKind.Info);
        else ShowToast(Loc.T("toast.import", added) + (skipped > 0 ? Loc.T("toast.added.skipped", skipped) : ""), ToastKind.Success);
    }

    async Task ImportFilesAsync()
    {
        var types = new FilePickerFileType(Loc.T("import.types")) { Patterns = new[] { "*.csv", "*.tsv", "*.json", "*.xml", "*.m3u", "*.m3u8", "*.zip", "*.txt" } };
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Loc.T("import.title"), AllowMultiple = true, FileTypeFilter = new[] { types, FilePickerFileTypes.All } });
        var tracks = new List<LibraryTrack>();
        var text = new StringBuilder();
        foreach (var file in files) if (file.TryGetLocalPath() is { } path) ReadDropped(path, tracks, text);
        if (files.Count > 0) AddGathered(tracks, text, ListNameOf(files.Select(f => f.TryGetLocalPath())));
    }

    // Songs with what is known about them first; the rest of the text may still be a table or a plain list.
    void AddGathered(List<LibraryTrack> tracks, StringBuilder text, string listName)
    {
        if (tracks.Count > 0) AddTracks(tracks);
        string rest = text.ToString();
        if (AddIfLibrary(rest, listName)) return;
        List<string> entries = Engine.ParseEntries(rest);
        if (entries.Count > 0) AddEntries(entries, "drop");
        else if (tracks.Count == 0) ShowToast(Loc.T("toast.import.none"), ToastKind.Warning);
    }

    // What the program was started with: links, names, or files dropped onto its icon.
    void AddArguments(string arguments)
    {
        var tracks = new List<LibraryTrack>();
        var text = new StringBuilder();
        foreach (string line in arguments.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (File.Exists(line) || Directory.Exists(line)) ReadDropped(line, tracks, text);
            else text.AppendLine(line);
        }
        AddGathered(tracks, text, "");
    }

    // One file gives the list its name; several have no single name.
    static string ListNameOf(IEnumerable<string?> paths)
    {
        var known = paths.OfType<string>().ToList();
        return known.Count == 1 ? Path.GetFileNameWithoutExtension(known[0]) : "";
    }

    // A library file gives songs with what is known about them; any other text file is read as a plain list.
    void ReadDropped(string path, List<LibraryTrack> tracks, StringBuilder text)
    {
        try
        {
            bool archive = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            if (path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xls", StringComparison.OrdinalIgnoreCase)) { ShowToast(Loc.T("toast.import.excel"), ToastKind.Warning); return; }
            if (!Directory.Exists(path) && !File.Exists(path)) return;
            if (!Directory.Exists(path) && !archive && new FileInfo(path).Length > LibraryImport.MaxFileBytes) { ShowToast(Loc.T("toast.import.big", Path.GetFileName(path)), ToastKind.Warning); return; }
            bool plain = path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
            if (LibraryImport.Handles(path) || plain)
            {
                string? content = plain || LibraryImport.IsTableFile(path) ? LibraryImport.ReadText(File.ReadAllBytes(path)) : null;
                var found = content != null ? LibraryImport.Parse(content, Path.GetFileNameWithoutExtension(path)) : LibraryImport.ReadFile(path);
                tracks.AddRange(found);
                // A table this program does not recognize, or a plain list: the text decides what happens next.
                if (found.Count == 0 && content != null) text.AppendLine(content);
            }
            else if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) // internet shortcuts: only the address, not the icon paths
                text.AppendLine(string.Join("\n", File.ReadAllLines(path).Where(l => l.TrimStart().StartsWith("URL=", StringComparison.OrdinalIgnoreCase)).Select(l => l.Trim()[4..])));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException) { ShowToast(ex.Message, ToastKind.Warning); }
    }

    // A list (several lines) or anything with a link goes straight to the queue; a single phrase stays editable.
    async Task PasteIntoInputAsync()
    {
        string? text;
        try { text = await ReadClipboardAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowToast(Loc.T("toast.clipboardbusy"), ToastKind.Warning); return; }
        if (string.IsNullOrEmpty(text)) return;
        if (AddIfLibrary(text)) return;
        if (Engine.ExtractUrls(text).Count == 0 && !text.Trim().AsSpan().ContainsAny('\r', '\n'))
        {
            string current = InputBox.Text ?? "";
            int caret = Math.Clamp(InputBox.CaretIndex, 0, current.Length);
            InputBox.Text = current.Insert(caret, text);
            InputBox.CaretIndex = caret + text.Length;
            return;
        }
        List<string> entries = Engine.ParseEntries(text);
        if (entries.Count == 0) return;
        lastClipboard = text;
        AddEntries(entries, "paste");
    }

    async Task AddFromClipboardAsync()
    {
        string? text;
        try { text = await ReadClipboardAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowToast(Loc.T("toast.clipboardbusy"), ToastKind.Warning); return; }
        if (AddIfLibrary(text)) return;
        List<string> entries = Engine.ParseEntries(text);
        if (entries.Count == 0) { ShowToast(Loc.T("toast.noentries"), ToastKind.Info); return; }
        lastClipboard = text ?? "";
        AddEntries(entries, "paste");
    }

    // There is no cross-platform "clipboard changed" signal, so it is looked at every moment. Reading is cheap.
    async Task CheckClipboardAsync()
    {
        if (!prefs.WatchClipboard) return;
        string? text;
        try { text = await ReadClipboardAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return; }
        if (text == null || text == lastClipboard) return;
        lastClipboard = text;
        // What was on the clipboard when watching began counts too: a link copied right before starting the app.
        if (text.Length > 20000) return;
        List<string> urls = Engine.ExtractUrls(text).Where(Engine.LooksLikeMedia).ToList();
        if (urls.Count > 0) AddEntries(urls, "clipboard");
    }

    void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (OverlayOpen) return;
        DropOverlay.IsVisible = true;
        e.DragEffects = DragDropEffects.Copy;
    }

    bool OverlayOpen => PickerOverlay.IsVisible || AuthOverlay.IsVisible || SetupOverlay.IsVisible || CloseOverlay.IsVisible || ClipOverlay.IsVisible || SettingsOverlay.IsVisible || ImportOverlay.IsVisible;

    void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        e.Handled = true;
        if (OverlayOpen) return;
        var text = new StringBuilder();
        string? dropped = e.DataTransfer.TryGetText();
        if (!string.IsNullOrEmpty(dropped)) text.AppendLine(dropped);
        var tracks = new List<LibraryTrack>();
        var files = e.DataTransfer.TryGetFiles() ?? Array.Empty<IStorageItem>();
        foreach (var item in files)
            if (item.TryGetLocalPath() is { } path) ReadDropped(path, tracks, text);
        // Handlers of a drop have nobody above them: whatever a strange file causes must stop here.
        try { AddGathered(tracks, text, ListNameOf(files.Select(f => f.TryGetLocalPath()))); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowToast(ex.Message, ToastKind.Error); }
    }

    // ---------- Filter ----------

    string CurrentFilter => FilterCheck.IsChecked == true ? "check" : FilterWaiting.IsChecked == true ? "waiting" : FilterActive.IsChecked == true ? "active" : FilterDone.IsChecked == true ? "done" : FilterFailed.IsChecked == true ? "failed" : "all";

    static bool Matches(QueueItem item, string filter, string text)
    {
        bool state = filter switch
        {
            "waiting" => item.State is ItemState.Fetching or ItemState.Ready or ItemState.Queued or ItemState.Cancelled,
            "active" => item.IsActive,
            "done" => item.State == ItemState.Done,
            "failed" => item.State == ItemState.Failed,
            "check" => item.ShowReview,
            _ => true
        };
        if (!state) return false;
        return text.Length == 0 || item.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) || item.Channel.Contains(text, StringComparison.CurrentCultureIgnoreCase)
            || (item.Query?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    void UpdateFilterSoon() { if (CurrentFilter != "all" || !string.IsNullOrEmpty(FilterBox.Text)) filterTimer.Start(); }

    void ApplyFilter()
    {
        filterSignature = 0;
        if (CurrentFilter == "all" && string.IsNullOrWhiteSpace(FilterBox.Text))
        {
            filterTimer.Stop();
            QueueList.ItemsSource = Queue.Items;
            NoMatch.IsVisible = false;
            return;
        }
        QueueList.ItemsSource = visible;
        RefreshFilter();
        filterTimer.Start();
    }

    // A filtered view is rebuilt when cards move between states, not on every progress tick.
    void RefreshFilter()
    {
        if (ReferenceEquals(QueueList.ItemsSource, Queue.Items)) { filterTimer.Stop(); return; }
        string filter = CurrentFilter, text = (FilterBox.Text ?? "").Trim();
        var wanted = Queue.Items.Where(i => Matches(i, filter, text)).ToList();
        int signature = wanted.Count;
        unchecked { foreach (var item in wanted) signature = signature * 31 + item.Number; }
        if (signature == filterSignature) return;
        filterSignature = signature;
        visible.Clear();
        foreach (var item in wanted) visible.Add(item);
        NoMatch.IsVisible = wanted.Count == 0 && Queue.Items.Count > 0;
    }

    // ---------- Summary ----------

    void UpdateSummary()
    {
        QueueSummary s = Queue.Summary();
        QueueCount.Text = s.Total.ToString(CultureInfo.CurrentCulture);
        EmptyState.IsVisible = s.Total == 0;
        FilterRow.IsVisible = s.Total > 0;
        RetryFailedButton.IsVisible = s.Failed > 0;
        CopyFailedButton.IsVisible = s.Failed > 0;
        StartText.Text = s.Startable > 0 ? Loc.T("settings.start.count", s.Startable) : Loc.T("settings.start");
        StopButton.IsVisible = s.Active + s.Queued > 0;
        OverallBar.IsVisible = s.Active + s.Queued + s.Fetching > 0;
        FilterAllText.Text = Loc.T("filter.all") + " " + s.Total.ToString(CultureInfo.CurrentCulture);
        FilterWaitingText.Text = Loc.T("filter.waiting") + " " + (s.Total - s.Done - s.Active - s.Failed).ToString(CultureInfo.CurrentCulture);
        FilterActiveText.Text = Loc.T("filter.active") + " " + s.Active.ToString(CultureInfo.CurrentCulture);
        FilterDoneText.Text = Loc.T("filter.done") + " " + s.Done.ToString(CultureInfo.CurrentCulture);
        FilterFailedText.Text = Loc.T("filter.failed") + " " + s.Failed.ToString(CultureInfo.CurrentCulture);
        FilterCheck.IsVisible = s.Review > 0;
        FilterCheckText.Text = Loc.T("filter.check") + " " + s.Review.ToString(CultureInfo.CurrentCulture);
        if (s.Review == 0 && FilterCheck.IsChecked == true) FilterAll.IsChecked = true;
        string pause = s.PausedUntil != null ? Loc.T("status.pause", s.PausedUntil) : "";
        if (s.Active + s.Queued > 0)
        {
            OverallBar.IsIndeterminate = false;
            OverallBar.Value = s.RunFraction * 100;
            taskbar.SetProgress(this, Math.Max(0.02, s.RunFraction));
            StatusText.Text = s.Queued > 0 ? Loc.T("status.running.queued", s.Active, s.Queued) : Loc.T("status.running", s.Active);
            StatusDetail.Text = Loc.T("status.running.detail", s.RunDone, s.RunTotal) + (s.Fetching > 0 ? Loc.T("status.running.fetching", s.Fetching) + pause : "");
            SpeedText.Text = s.Speed > 0 ? "↓ " + Format.Speed(s.Speed) : "";
        }
        else
        {
            OverallBar.IsIndeterminate = s.Fetching > 0;
            OverallBar.Value = 0;
            taskbar.Clear(this);
            SpeedText.Text = "";
            StatusText.Text = s.Fetching > 0 ? Loc.T("status.fetching.count", s.Fetching) + pause : s.Total == 0 ? Loc.T("status.ready.idle") : Loc.T("status.list", s.Total);
            StatusDetail.Text = s.Total == 0 ? Loc.T("status.ready.detail")
                : s.Done > 0 ? (s.Failed > 0 ? Loc.T("status.list.done.failed", s.Done, s.Failed) : Loc.T("status.list.done", s.Done))
                : Loc.T("status.list.detail");
        }
    }

    void OnRunCompleted(int done, int failed)
    {
        if (!checkTools) return;
        if (prefs.Sound) Desktop.PlayDoneSound(failed > 0);
        if (prefs.OpenFolder && done > 0) TryOpen(prefs.Folder);
    }

    // ---------- Feedback ----------

    public void ShowToast(string text, ToastKind kind)
    {
        ToastGlyph.Text = kind switch { ToastKind.Success => "", ToastKind.Warning => "", ToastKind.Error => "", _ => "" };
        ToastGlyph[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(kind switch { ToastKind.Success => "Good", ToastKind.Warning => "Warn", ToastKind.Error => "Bad", _ => "Info" });
        ToastText.Text = text;
        Toast.Opacity = 1;
        toastTimer.Stop();
        toastTimer.Interval = TimeSpan.FromSeconds(kind is ToastKind.Error or ToastKind.Warning ? 5.5 : 3.2);
        toastTimer.Start();
    }

    void Log(string line)
    {
        // Lines are collected and shown a few times a second; appending them one by one slows the window down
        // when a long list produces thousands of lines.
        logPending.Append(line).Append('\n');
        if (!checkTools) return;
        try
        {
            logWriter ??= new StreamWriter(Path.Combine(AppPaths.Logs, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture) + ".log"), false, new UTF8Encoding(false));
            logWriter.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    void FlushLog()
    {
        try { logWriter?.Flush(); } catch (IOException) { }
        if (logPending.Length == 0) return;
        // The window keeps the latest part; the full history stays in the log file.
        if (logPending.Length > 200000) logPending.Remove(0, logPending.Length - 150000);
        string current = logLength + logPending.Length > 200000 ? "" : LogBox.Text ?? "";
        if (current.Length == 0) logLength = 0;
        LogBox.Text = current + logPending;
        logLength += logPending.Length;
        logPending.Clear();
        if (LogPanel.IsVisible) LogBox.CaretIndex = int.MaxValue;
    }

    static void PruneLogs()
    {
        try
        {
            foreach (var file in new DirectoryInfo(AppPaths.Logs).GetFiles("*.log").OrderByDescending(f => f.Name).Skip(40)) file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---------- Tools ----------

    async Task EnsureToolsAsync()
    {
        PruneLogs();
        YtDlpBackend.PruneThumbnails();
        Auth.CleanTemporaryCopies();
        if (ToolManager.Missing().Count == 0) { SetupOverlay.IsVisible = false; return; }
        SetupOverlay.IsVisible = true;
        SetupActions.IsVisible = false;
        SetupBar.IsIndeterminate = true;
        setupCancel = new CancellationTokenSource();
        var progress = new Progress<ToolProgress>(p =>
        {
            SetupStatus.Text = p.Message;
            SetupBar.IsIndeterminate = p.Fraction < 0;
            if (p.Fraction >= 0) SetupBar.Value = p.Fraction;
        });
        try
        {
            await ToolManager.InstallMissingAsync(progress, setupCancel.Token, prefs.Proxy);
            SetupOverlay.IsVisible = false;
            _ = LoadVersionAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetupBar.IsIndeterminate = false;
            SetupBar.Value = 0;
            SetupStatus.Text = Loc.T("setup.failed", ex.Message);
            SetupActions.IsVisible = true;
        }
    }

    // Only says so: replacing a running program is for the person to choose.
    async Task CheckForNewVersionAsync()
    {
        try
        {
            if (await AppUpdate.NewerVersionAsync(prefs.Proxy) is not { } version) return;
            NewVersionText.Text = Loc.T("header.newversion", version);
            NewVersionButton.IsVisible = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { Log("New version check failed: " + ex.Message); }
    }

    async Task LoadVersionAsync()
    {
        if (ToolManager.Find("yt-dlp") is not { } exe) return;
        string? version = null;
        try
        {
            using var runner = new ProcessRunner();
            await runner.RunAsync(exe, ["--version"], line => { if (version == null && line.Trim().Length > 0) version = line.Trim(); });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        VersionText.Text = version != null ? "yt-dlp " + version : "";
    }

    // Quiet is the check made by itself on start: it speaks up only if something changed.
    async Task UpdateYtDlpAsync(bool quiet)
    {
        if (updating) return;
        if (Queue.AnyActive)
        {
            if (!quiet) ShowToast(Loc.T("toast.wait.update"), ToastKind.Info);
            return;
        }
        updating = true;
        UpdateButton.IsEnabled = false;
        // Nothing may start yt-dlp while its file is being replaced.
        Queue.Hold = true;
        if (!quiet) ShowToast(Loc.T("toast.update.checking"), ToastKind.Info);
        ToolUpdate? result = null;
        try { result = await backend.UpdateToolAsync(prefs.Proxy, false, line => Dispatcher.UIThread.Post(() => Log(line))); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log(ex.Message); }
        updating = false;
        UpdateButton.IsEnabled = true;
        Queue.Hold = false;
        if (result is not { Ok: true })
        {
            if (!quiet) ShowToast(Loc.T("toast.update.failed"), ToastKind.Error);
        }
        else if (result.Changed || !quiet) ShowToast(Loc.T(result.Changed ? "toast.update.done" : "toast.update.current"), ToastKind.Success);
        await LoadVersionAsync();
    }

    // ---------- Version picker ----------

    public void OpenPicker(QueueItem item)
    {
        if (item.State == ItemState.Fetching) { ShowToast(Loc.T("status.fetching"), ToastKind.Info); return; }
        if (!item.CanEdit) { ShowToast(Loc.T("toast.alldone"), ToastKind.Info); return; }
        pickerTarget = item;
        PickerQuery.Text = item.Query ?? (item.IsOriginal ? item.Channel + " - " + item.Title : item.Title);
        PickerOverlay.IsVisible = true;
        PickerQuery.Focus();
        PickerQuery.SelectAll();
        // The tab follows what the card shows now; setting it must not start a second search.
        pickerTabSwitching = true;
        bool music = Queue.PrefersTracks(item);
        PickerMusic.IsChecked = music;
        PickerVideos.IsChecked = !music;
        pickerTabSwitching = false;
        if (item.Alternatives is { Count: > 0 }) ShowCandidates(item.Alternatives);
        else _ = PickerSearchAsync();
    }

    void OnPickerTabChanged(RadioButton tab)
    {
        if (pickerTabSwitching || pickerTarget == null || tab.IsChecked != true) return;
        var known = tab == PickerMusic ? pickerTarget.TrackAlternatives : pickerTarget.ClipAlternatives;
        if (known is { Count: > 0 }) ShowCandidates(known);
        else _ = PickerSearchAsync();
    }

    void ShowCandidates(List<Dictionary<string, object?>> list)
    {
        if (pickerTarget == null) return;
        candidates.Clear();
        foreach (var info in list)
        {
            QueueItem candidate = QueueController.MakeCandidate(info, pickerTarget);
            candidates.Add(candidate);
            _ = Queue.LoadThumbAsync(candidate);
        }
        PickerStatus.Text = list.Count == 0 ? Loc.T("picker.none") : Loc.T("picker.count", list.Count);
    }

    async Task PickerSearchAsync()
    {
        string query = (PickerQuery.Text ?? "").Trim();
        QueueItem? target = pickerTarget;
        if (query.Length < 2 || target == null) { PickerStatus.Text = Loc.T("picker.query"); return; }
        pickerRunner?.Cancel();
        var runner = new ProcessRunner();
        pickerRunner = runner;
        candidates.Clear();
        PickerStatus.Text = Loc.T("picker.searching");
        try
        {
            var entries = await Queue.SearchVersionsAsync(target, query, PickerMusic.IsChecked == true, runner);
            if (pickerRunner == runner && pickerTarget == target) ShowCandidates(entries);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (pickerRunner == runner) PickerStatus.Text = Loc.T("picker.failed", ex.Message); }
        finally
        {
            runner.Dispose();
            if (pickerRunner == runner) pickerRunner = null;
        }
    }

    void ClosePicker()
    {
        PickerOverlay.IsVisible = false;
        pickerRunner?.Cancel();
        pickerRunner = null;
        pickerTarget = null;
        candidates.Clear();
    }

    // ---------- A part of a video ----------

    void OpenClip(QueueItem item)
    {
        string[] ends = (item.Section ?? "").Split('-');
        clipTarget = item;
        ClipFrom.Text = ends.Length == 2 ? Format.Duration(double.Parse(ends[0], CultureInfo.InvariantCulture)) : "";
        ClipTo.Text = ends.Length == 2 ? Format.Duration(double.Parse(ends[1], CultureInfo.InvariantCulture)) : "";
        ClipTo.PlaceholderText = item.Duration;
        ClipOverlay.IsVisible = true;
        ClipFrom.Focus();
    }

    void ApplyClip(string? from, string? to)
    {
        if (clipTarget == null) return;
        if (!Queue.SetSection(clipTarget, from, to)) { ShowToast(Loc.T("toast.clip.bad"), ToastKind.Warning); return; }
        ClipOverlay.IsVisible = false;
        clipTarget = null;
    }

    // ---------- Sign-in ----------

    void UpdateAccount()
    {
        AccountText.Text = Auth.SignedIn ? Loc.T("auth.signedin") : Loc.T("auth.button");
        AccountDot.IsVisible = Auth.SignedIn;
        ToolTip.SetTip(AccountButton, Auth.SignedIn ? Loc.T("auth.tip.signedin") + " (" + Auth.Describe() + ")" : Loc.T("auth.tip"));
        AuthSignOut.IsVisible = Auth.SignedIn;
        if (AccountMenu != null) AccountMenu.IsEnabled = Auth.SignedIn;
    }

    public void OpenAuth()
    {
        browsers = Browsers.Installed();
        BrowserBox.ItemsSource = browsers.Select(b => b.IsDefault ? b.Name + " (" + Loc.T("auth.default") + ")" : b.Name).ToList();
        BrowserBox.SelectedIndex = browsers.Count > 0 ? Math.Max(0, Auth.Browser != null ? browsers.FindIndex(b => b.Id == Auth.Browser) : 0) : -1;
        AuthUse.IsEnabled = browsers.Count > 0;
        AuthSeparate.IsEnabled = browsers.Count > 0;
        SetAuthStatus(browsers.Count == 0 ? Loc.T("auth.nobrowser") : Auth.SignedIn ? Loc.T("auth.ok", Auth.Describe()) : null, false);
        UpdateAccount();
        AuthOpenPage.IsVisible = false;
        AuthOverlay.IsVisible = true;
    }

    void CloseAuth()
    {
        signInCancel?.Cancel();
        AuthOverlay.IsVisible = false;
    }

    void SetAuthStatus(string? text, bool busy)
    {
        AuthStatus.Text = text ?? "";
        AuthStatus.IsVisible = text != null;
        AuthBusy.IsVisible = busy;
    }

    BrowserInfo? SelectedBrowser => BrowserBox.SelectedIndex >= 0 && BrowserBox.SelectedIndex < browsers.Count ? browsers[BrowserBox.SelectedIndex] : null;

    // The default path: the browser the person already uses, already signed in.
    async Task UseBrowserAsync()
    {
        if (SelectedBrowser is not { } browser) return;
        if (ToolManager.Find("yt-dlp") == null) { SetAuthStatus(Loc.T("tools.missing", "yt-dlp"), false); return; }
        SetAuthStatus(Loc.T("auth.checking", browser.Name), true);
        AuthUse.IsEnabled = false;
        try
        {
            SignInCheck check = await Auth.CheckBrowserAsync(browser.Id);
            Log("Sign-in check for " + browser.Id + ": " + check.Status);
            switch (check.Status)
            {
                case SignInStatus.SignedIn:
                    Auth.UseBrowser(browser.Id);
                    SignInDone();
                    break;
                case SignInStatus.NotSignedIn:
                    // Its sign-ins to other sites are still good, so it is used either way.
                    Auth.UseBrowser(browser.Id);
                    UpdateAccount();
                    SetAuthStatus(Loc.T("auth.notsignedin", browser.Name), false);
                    AuthOpenPage.IsVisible = true;
                    break;
                default:
                    if (check.Detail.Length > 0) Log(check.Detail);
                    SetAuthStatus(browser.Id == "safari" ? Loc.T("auth.cannotread.safari") : Loc.T("auth.cannotread", browser.Name), false);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { SetAuthStatus(ex.Message, false); }
        finally { AuthUse.IsEnabled = true; }
    }

    void SignInDone()
    {
        SetAuthStatus(null, false);
        AuthOverlay.IsVisible = false;
        UpdateAccount();
        Log("Signed in " + Auth.Describe());
        Queue.SignedIn();
    }

    async Task SeparateSignInAsync()
    {
        if (SelectedBrowser is not { } browser) return;
        // Safari and a few others cannot run with a separate profile; another installed browser can stand in.
        BrowserInfo? usable = Browsers.Executable(browser.Id) != null ? browser : browsers.FirstOrDefault(b => Browsers.Executable(b.Id) != null);
        if (usable == null) { SetAuthStatus(Loc.T("auth.separate.none", browser.Name), false); return; }
        signInCancel = new CancellationTokenSource();
        SetAuthStatus(Loc.T("auth.separate.wait"), true);
        try
        {
            SignInCheck check = await SeparateSignIn.RunAsync(usable.Id, signInCancel.Token);
            if (check.Status == SignInStatus.SignedIn) SignInDone();
            else SetAuthStatus(Loc.T("auth.separate.fail"), false);
        }
        catch (OperationCanceledException) { SetAuthStatus(null, false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { SetAuthStatus(ex.Message, false); }
    }

    async Task ImportCookiesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Loc.T("auth.importfile"), AllowMultiple = false });
        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path == null) return;
        try
        {
            if (Auth.ImportCookieFile(path)) SignInDone();
            else SetAuthStatus(Loc.T("auth.file.bad"), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetAuthStatus(ex.Message, false); }
    }

    void SignOut()
    {
        Auth.SignOut();
        UpdateAccount();
        SetAuthStatus(Loc.T("auth.signedout"), false);
        Log("Signed out here");
        ShowToast(Loc.T("auth.signedout"), ToastKind.Success);
    }

    // ---------- Closing ----------

    void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (Queue.AnyActive && !reallyClose && checkTools)
        {
            e.Cancel = true;
            CloseOverlay.IsVisible = true;
            return;
        }
        pickerRunner?.Cancel();
        signInCancel?.Cancel();
        setupCancel?.Cancel();
        signInCancel?.Dispose();
        setupCancel?.Dispose();
        signInCancel = setupCancel = null;
        Queue.Dispose();
        prefs.Folder = (FolderBox.Text ?? "").Trim();
        prefs.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) { prefs.WindowWidth = Bounds.Width; prefs.WindowHeight = Bounds.Height; }
        if (checkTools) prefs.Save();
        FlushLog();
        logWriter?.Dispose();
        logWriter = null;
    }
}

/// <summary>Progress on the Windows taskbar button. Other systems have no common equivalent.</summary>
sealed class Taskbar
{
    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)] class TaskbarInstance { }

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
    }

    ITaskbarList3? list;
    bool failed;

    ITaskbarList3? Get()
    {
        if (!OperatingSystem.IsWindows() || failed) return null;
        if (list != null) return list;
        try
        {
            list = (ITaskbarList3)new TaskbarInstance();
            list.HrInit();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { failed = true; list = null; }
        return list;
    }

    public void SetProgress(Window window, double fraction)
    {
        if (Get() is not { } bar || window.TryGetPlatformHandle()?.Handle is not { } handle) return;
        try { bar.SetProgressValue(handle, (ulong)(fraction * 1000), 1000); }
        catch (COMException) { failed = true; }
    }

    public void Clear(Window window)
    {
        if (Get() is not { } bar || window.TryGetPlatformHandle()?.Handle is not { } handle) return;
        try { bar.SetProgressState(handle, 0); }
        catch (COMException) { failed = true; }
    }
}
