using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CodexUsageWidget;

public sealed class WidgetWindow : Window
{
    readonly SettingsStore store;
    internal readonly Settings Preferences;
    readonly IUsageProvider provider;
    readonly CancellationTokenSource lifetime = new();
    readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    readonly StackPanel rows = new();
    readonly TextBlock status = new();
    readonly TextBlock pin = new();
    readonly Grid surface = new();
    readonly Border tint = new();
    readonly Image backdrop = new() { Stretch = Stretch.UniformToFill };
    readonly Button refresh;
    readonly MenuItem refreshMenu;
    internal readonly Dictionary<string, MenuItem> Toggles = new();
    internal readonly Dictionary<string, MenuItem> ThemeItems = new();
    internal readonly Dictionary<string, Button> UsageButtons = new();
    internal bool LightTheme { get; private set; }
    readonly MenuItem glassMenu;
    readonly MenuItem imageMenu;
    readonly TextBlock brandMark;
    internal UsageSnapshot? Snapshot;
    internal bool Busy { get; private set; }
    string? failure;
    string? backgroundWarning;
    bool ready, closed;
    int failures;
    DateTimeOffset lastAttempt = DateTimeOffset.MinValue;
    readonly bool testing;
    TrayIcon? tray;
    HwndSource? windowSource;
    ContextMenu? trayMenu;
    bool backdropQueued;
    internal BackdropResult? BackdropState { get; private set; }
    internal Func<string, string, bool>? NotificationSink;
    Brush White => (Brush)Application.Current.Resources["WidgetText"];
    Brush Muted => (Brush)Application.Current.Resources["WidgetMuted"];
    Brush Mint => (Brush)Application.Current.Resources["WidgetAccent"];
    Brush ColorResource(string key) => (Brush)Application.Current.Resources[key];
    static SolidColorBrush Brush(string value) => (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;

    public WidgetWindow(SettingsStore store, IUsageProvider? provider = null, bool testing = false)
    {
        this.store = store; this.testing = testing; Preferences = store.Load();
        LightTheme = Appearance.IsLight(Preferences.Theme, Appearance.SystemIsLight());
        Appearance.Apply(Application.Current.Resources, LightTheme);
        this.provider = provider ?? new CodexProvider(() => Preferences.CodexPath);
        Title = "Codex Usage Widget";
        ShowActivated = !testing;
        ShowInTaskbar = false;
        Width = Preferences.Width; Height = Preferences.Height;
        MinWidth = 280; MinHeight = 180; MaxWidth = 800; MaxHeight = 900;
        Left = Preferences.Left; Top = Preferences.Top;
        WindowStartupLocation = WindowStartupLocation.Manual; WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize; Background = Brushes.Transparent;
        Foreground = White; FontFamily = new FontFamily("Segoe UI, Yu Gothic UI"); FontSize = 12;
        Topmost = Preferences.AlwaysOnTop;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(-1), CornerRadius = new CornerRadius(16), UseAeroCaptionButtons = false });
        surface.Children.Add(backdrop); surface.Children.Add(tint);
        var outline = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(15), IsHitTestVisible = false };
        outline.SetResourceReference(Border.BorderBrushProperty, "WidgetBorder");
        surface.Children.Add(outline);
        var layout = new Grid { Margin = new Thickness(20, 14, 20, 14) };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        surface.Children.Add(layout); Content = surface;
        var header = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 14), Cursor = Cursors.SizeAll };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brandMark = new TextBlock { Text = "◈", Foreground = Mint, FontSize = 20, Margin = new Thickness(0, 0, 8, 0) };
        brand.Children.Add(brandMark);
        brand.Children.Add(new TextBlock { Text = "CODEX", FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        pin.Text = "  • PIN"; pin.FontSize = 10; pin.Foreground = Mint; pin.VerticalAlignment = VerticalAlignment.Center;
        brand.Children.Add(pin); header.Children.Add(brand);
        header.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) { try { DragMove(); } catch (InvalidOperationException) { } } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        refresh = MakeButton("↻", "今すぐ更新 (F5)", async () => await RefreshAsync());
        actions.Children.Add(refresh);
        actions.Children.Add(MakeButton("⋯", "設定メニュー (右クリック)", () => { ContextMenu.PlacementTarget = this; ContextMenu.Placement = PlacementMode.MousePoint; ContextMenu.IsOpen = true; }));
        actions.Children.Add(MakeButton("×", "終了", Close));
        Grid.SetColumn(actions, 1); header.Children.Add(actions); layout.Children.Add(header);
        var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 3, 0) };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        status.Foreground = Muted; status.FontSize = 10; status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 10, 5, 0);
        Grid.SetRow(status, 2); layout.Children.Add(status);
        var grip = new ResizeGrip { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Width = 16, Height = 16, Opacity = .65 };
        surface.Children.Add(grip);

        ContextMenu = new ContextMenu { FontFamily = FontFamily, FontSize = 12 };
        refreshMenu = Item("今すぐ更新    F5", async () => await RefreshAsync());
        ContextMenu.Items.Add(refreshMenu); ContextMenu.Items.Add(new Separator());
        Toggle("5h", "5h を表示", () => Preferences.ShowFiveHour, v => Preferences.ShowFiveHour = v);
        Toggle("Week", "Week を表示", () => Preferences.ShowWeek, v => Preferences.ShowWeek = v);
        Toggle("Reset", "Reset時刻を表示", () => Preferences.ShowReset, v => Preferences.ShowReset = v);
        Toggle("Credits", "Codex Credits を表示", () => Preferences.ShowCredits, v => Preferences.ShowCredits = v);
        ContextMenu.Items.Add(new Separator());
        Toggle("Topmost", "常時最前面", () => Preferences.AlwaysOnTop, v => { Preferences.AlwaysOnTop = v; Topmost = v; });
        Toggle("Notifications", "残量5%未満でWindows通知", () => Preferences.LowUsageNotifications, v => Preferences.LowUsageNotifications = v);
        ContextMenu.Items.Add(Item("通知をテスト", () => SendNotification("Codex Usage Widget", "テスト通知です。残量5%未満の通知は設定でON/OFFできます。")));
        var themes = new MenuItem { Header = "テーマ" };
        foreach (var (mode, label) in new[] { ("Dark", "ダークモード"), ("Light", "ライトモード"), ("System", "システムと同期") })
        {
            var item = Item(label, () => SetTheme(mode)); item.IsCheckable = true; item.IsChecked = Preferences.Theme == mode;
            ThemeItems[mode] = item; themes.Items.Add(item);
        }
        ContextMenu.Items.Add(themes);
        ContextMenu.Items.Add(new Separator());
        ContextMenu.Items.Add(Item("背景画像を選択…", () => Dispatcher.BeginInvoke(SelectImage, DispatcherPriority.Background)));
        glassMenu = Item("ガラス効果（OFFで単色）", () => SetGlass(glassMenu!.IsChecked));
        glassMenu.IsCheckable = true; ContextMenu.Items.Add(glassMenu);
        imageMenu = Item("前回の背景画像を使用", () => { Preferences.ImageBackground = true; ApplyBackground(); Persist(); });
        imageMenu.IsCheckable = true; ContextMenu.Items.Add(imageMenu);
        ContextMenu.Items.Add(new Separator());
        ContextMenu.Items.Add(Item("位置・サイズを戻す", () => { Width = 340; Height = 290; Left = SystemParameters.WorkArea.Right - Width - 30; Top = SystemParameters.WorkArea.Top + 30; Persist(); }));
        var connection = new MenuItem { Header = "Codex接続" };
        connection.Items.Add(Item("codex.exe を選択…", () => { var d = new OpenFileDialog { Title = "Codex実行ファイルを選択", Filter = "Codex executable|codex.exe", CheckFileExists = true }; if (d.ShowDialog(this) == true) { Preferences.CodexPath = d.FileName; Snapshot = null; Persist(); _ = RefreshAsync(); } }));
        connection.Items.Add(Item("実行ファイルを自動検出", () => { Preferences.CodexPath = null; Snapshot = null; Persist(); _ = RefreshAsync(); }));
        ContextMenu.Items.Add(connection);
        ContextMenu.Items.Add(Item("このアプリについて", () => MessageBox.Show(this, "Codex Usage Widget 1.2\n\nバーを左クリックすると使用率／残量を切り替えます。\n5hとWeekの表示モードは個別に保存します。\nテーマ: ダーク／ライト／システムと同期。\n5分ごとに自動更新。失敗時は最大30分まで間隔を延長します。\nResetはWindowsの現地時刻です。\nCreditsは追加利用残高で、利用枠リセット券とは別です。\n\n上部をドラッグして移動、端をドラッグしてリサイズ。\n右クリックまたは Shift+F10 で設定。\n\nログイン済みのCodexが必要です。認証管理はCodexに任せ、ウィジェットは認証情報を保存しません。\n\n設定: " + store.FilePath, "Codex Usage Widget", MessageBoxButton.OK, MessageBoxImage.Information)));
        ContextMenu.Items.Add(new Separator());
        ContextMenu.Items.Add(Item("ウィジェットを隠す", HideToTray));
        ContextMenu.Items.Add(Item("終了", Close));
        KeyDown += async (_, e) => { if (e.Key == Key.F5) { e.Handled = true; await RefreshAsync(); } };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Persist(); };
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        clockTimer.Tick += (_, _) => Render();
        LocationChanged += (_, _) => QueueSave();
        SizeChanged += (_, _) => { surface.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 15, 15); QueueSave(); };
        SourceInitialized += (_, _) =>
        {
            windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            windowSource?.AddHook(WindowMessage);
            if (!testing && windowSource != null) tray = new TrayIcon(windowSource, RestoreFromTray, OpenTrayMenu);
            KeepOnScreen(); ApplyBackground();
        };
        Loaded += async (_, _) => { ready = true; QueueBackdrop(); Render(); if (!testing) { Persist(); refreshTimer.Start(); clockTimer.Start(); await RefreshAsync(); } };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) HideToTray(); };
        Closing += (_, _) => { Persist(); closed = true; lifetime.Cancel(); refreshTimer.Stop(); clockTimer.Stop(); saveTimer.Stop(); };
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        Activated += (_, _) => { if (Preferences.Theme == "System") RefreshSystemTheme(); QueueBackdrop(); };
        Closed += (_, _) => { trayMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false); tray?.Dispose(); windowSource?.RemoveHook(WindowMessage); SystemEvents.DisplaySettingsChanged -= DisplayChanged; SystemEvents.UserPreferenceChanged -= SystemThemeChanged; lifetime.Dispose(); };
        Render();
    }
    void DisplayChanged(object? sender, EventArgs e) { if (!closed) Dispatcher.BeginInvoke(() => { if (!closed) KeepOnScreen(); }); }
    void SystemThemeChanged(object sender, UserPreferenceChangedEventArgs e) { if (!closed) Dispatcher.BeginInvoke(() => { if (closed) return; if (Preferences.Theme == "System") RefreshSystemTheme(); QueueBackdrop(); }); }
    IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x31a or 0x31e or 0x1a) QueueBackdrop();
        return IntPtr.Zero;
    }
    void QueueBackdrop()
    {
        if (closed || backdropQueued) return;
        backdropQueued = true;
        Dispatcher.BeginInvoke(() => { backdropQueued = false; if (!closed) ApplyBackground(); }, DispatcherPriority.Loaded);
    }
    internal void HideToTray()
    {
        if (!testing && tray?.Registered != true) return;
        Persist(); Hide();
    }
    internal void RestoreFromTray()
    {
        if (closed) return;
        Show(); WindowState = WindowState.Normal; Activate(); QueueBackdrop();
    }
    void OpenTrayMenu()
    {
        trayMenu = new ContextMenu();
        trayMenu.Items.Add(Item("ウィジェットを表示", RestoreFromTray));
        trayMenu.Items.Add(Item("ウィジェットを隠す", HideToTray));
        trayMenu.Items.Add(new Separator());
        trayMenu.Items.Add(Item("今すぐ更新", async () => await RefreshAsync()));
        var notification = new MenuItem { Header = "残量5%未満でWindows通知", IsCheckable = true, IsChecked = Preferences.LowUsageNotifications };
        notification.Click += (_, _) => { Preferences.LowUsageNotifications = notification.IsChecked; Toggles["Notifications"].IsChecked = notification.IsChecked; Persist(); };
        trayMenu.Items.Add(notification);
        trayMenu.Items.Add(Item("設定を開く", () => { RestoreFromTray(); Dispatcher.BeginInvoke(() => { ContextMenu.PlacementTarget = this; ContextMenu.Placement = PlacementMode.Bottom; ContextMenu.IsOpen = true; }); }));
        trayMenu.Items.Add(new Separator()); trayMenu.Items.Add(Item("終了", Close));
        trayMenu.Placement = PlacementMode.MousePoint; trayMenu.IsOpen = true;
    }
    bool SendNotification(string title, string message) => testing ? NotificationSink?.Invoke(title, message) == true : tray?.Notify(title, message) == true;
    internal void ProcessAlerts(UsageSnapshot snapshot)
    {
        var alerts = LowUsageAlerts.Evaluate(Preferences, snapshot, DateTimeOffset.Now);
        if (alerts.Count > 0 && SendNotification("Codexの残量が5%未満です", LowUsageAlerts.Message(alerts)))
            LowUsageAlerts.MarkSent(Preferences, alerts);
        store.Save(Preferences);
    }
    internal void SetGlass(bool enabled)
    {
        Preferences.GlassEnabled = enabled; Preferences.ImageBackground = false;
        ApplyBackground(); QueueBackdrop(); Persist();
    }
    void RefreshSystemTheme() { bool next = Appearance.IsLight(Preferences.Theme, Appearance.SystemIsLight()); if (next != LightTheme) ApplyTheme(); }
    internal void SetTheme(string mode)
    {
        Preferences.Theme = mode; Preferences.Normalize(); ApplyTheme(); Persist();
    }
    void ApplyTheme()
    {
        LightTheme = Appearance.IsLight(Preferences.Theme, Appearance.SystemIsLight());
        Appearance.Apply(Application.Current.Resources, LightTheme);
        Foreground = White; status.Foreground = Muted; pin.Foreground = Mint; brandMark.Foreground = Mint;
        foreach (var (mode, item) in ThemeItems) item.IsChecked = mode == Preferences.Theme;
        ApplyBackground();
    }
    Button MakeButton(string text, string tooltip, Action action)
    {
        var b = new Button { Content = text, ToolTip = tooltip, Width = 28, Height = 28, Margin = new Thickness(2, 0, 0, 0), Foreground = White, Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 18, Cursor = Cursors.Hand };
        System.Windows.Automation.AutomationProperties.SetName(b, tooltip);
        b.SetResourceReference(Control.ForegroundProperty, "WidgetText");
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7)); border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(presenter);
        b.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        b.MouseEnter += (_, _) => b.Background = ColorResource("WidgetHover"); b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.Click += (_, e) => { e.Handled = true; action(); }; return b;
    }
    MenuItem Item(string label, Action action) { var m = new MenuItem { Header = label }; m.Click += (_, e) => { e.Handled = true; action(); }; return m; }
    void Toggle(string key, string label, Func<bool> get, Action<bool> set)
    {
        var m = new MenuItem { Header = label, IsCheckable = true, IsChecked = get() };
        m.Click += (_, _) => { set(m.IsChecked); Render(); Persist(); };
        Toggles[key] = m; ContextMenu.Items.Add(m);
    }
    internal void SelectImageFile(string file)
    {
        var imported = store.ImportImage(file);
        Preferences.BackgroundImage = imported; Preferences.ImageBackground = true; ApplyBackground(); Persist();
    }
    void SelectImage()
    {
        var dialog = new OpenFileDialog { Title = "背景画像を選択", Filter = "画像 (PNG / JPEG / BMP / TIFF)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff", CheckFileExists = true, Multiselect = false };
        try { if (dialog.ShowDialog(this) == true) SelectImageFile(dialog.FileName); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FileFormatException)
        { MessageBox.Show(this, "画像を読み込めませんでした。30MB以下のPNG/JPEG/BMP画像を選択してください。", "背景画像", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    void ApplyBackground()
    {
        backgroundWarning = null; backdrop.Source = null;
        if (Preferences.ImageBackground && Preferences.BackgroundImage != null)
        {
            try { backdrop.Source = SettingsStore.LoadImage(Preferences.BackgroundImage); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FileFormatException)
            { backgroundWarning = "背景画像を読み込めないため標準背景を表示中。"; }
        }
        bool image = backdrop.Source != null;
        BackdropState = WindowBackdrop.Apply(this, Preferences.GlassEnabled, image, LightTheme);
        bool acrylic = BackdropState.GlassActive;
        if (Preferences.GlassEnabled && !image && !acrylic) backgroundWarning ??= "この環境ではガラス効果を適用できないため単色表示中。";
        tint.Background = LightTheme
            ? new LinearGradientBrush(image ? Color.FromArgb(200, 250, 252, 255) : acrylic ? Color.FromArgb(55, 245, 249, 255) : Color.FromRgb(239, 245, 252), image ? Color.FromArgb(224, 245, 249, 255) : acrylic ? Color.FromArgb(95, 237, 244, 252) : Color.FromRgb(224, 235, 248), 75)
            : new LinearGradientBrush(image ? Color.FromArgb(175, 10, 18, 30) : acrylic ? Color.FromArgb(45, 17, 28, 44) : Color.FromRgb(27, 40, 58), image ? Color.FromArgb(205, 10, 18, 30) : acrylic ? Color.FromArgb(95, 15, 23, 37) : Color.FromRgb(15, 23, 37), 75);
        glassMenu.IsChecked = !Preferences.ImageBackground && Preferences.GlassEnabled;
        imageMenu.IsChecked = Preferences.ImageBackground;
        imageMenu.IsEnabled = Preferences.BackgroundImage != null;
        Render();
    }
    internal async Task RefreshAsync()
    {
        if (Busy || closed || (!testing && DateTimeOffset.Now - lastAttempt < TimeSpan.FromSeconds(10))) return;
        lastAttempt = DateTimeOffset.Now; Busy = true; refresh.IsEnabled = false; refreshMenu.IsEnabled = false; Render();
        try { Snapshot = await provider.ReadAsync(lifetime.Token); failure = null; failures = 0; ProcessAlerts(Snapshot); }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception e) { failure = e is UsageException ? e.Message : "更新に失敗しました。接続とCodexの状態を確認してください。"; failures++; }
        finally
        {
            Busy = false;
            if (!closed)
            {
                refresh.IsEnabled = true; refreshMenu.IsEnabled = true;
                refreshTimer.Interval = TimeSpan.FromMinutes(Math.Min(30, 5 * Math.Pow(2, Math.Min(3, failures))));
                if (!testing) { refreshTimer.Stop(); refreshTimer.Start(); }
                Render();
            }
        }
    }
    internal void Render()
    {
        rows.Children.Clear(); UsageButtons.Clear(); pin.Visibility = Preferences.AlwaysOnTop ? Visibility.Visible : Visibility.Collapsed;
        if (Preferences.ShowFiveHour) UsageRow("5h", Snapshot?.FiveHour, Mint);
        if (Preferences.ShowWeek) UsageRow("Week", Snapshot?.Week, ColorResource("WidgetWeek"));
        if (Preferences.ShowReset)
        {
            if (!Preferences.ShowFiveHour) ResetRow("5h", Snapshot?.FiveHour);
            if (!Preferences.ShowWeek) ResetRow("Week", Snapshot?.Week);
        }
        if (Preferences.ShowCredits)
        {
            var credit = new Grid { Margin = new Thickness(0, 2, 0, 12) };
            credit.Children.Add(new TextBlock { Text = "Codex Credits", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            credit.Children.Add(new TextBlock { Text = Snapshot?.Credits ?? "取得不可", FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right });
            credit.ToolTip = "追加利用のCredits残高。利用枠リセット券の数とは異なります。未提供の残高を0と推測しません。";
            rows.Children.Add(credit);
        }
        if (rows.Children.Count == 0) rows.Children.Add(new TextBlock { Text = "表示項目を右クリックで選択", Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        var age = Snapshot == null ? "未取得" : "更新 " + Snapshot.FetchedAt.ToLocalTime().ToString("HH:mm");
        status.Text = Busy ? "更新中…" : failure != null ? failure + (Snapshot != null ? "\n前回値 · " + age : "") : age + "  ·  自動更新 5分";
        if (Snapshot != null && failure == null && DateTimeOffset.Now - Snapshot.FetchedAt > TimeSpan.FromMinutes(7)) status.Text += "  ·  前回値";
        if (store.Warning != null) status.Text += "\n" + store.Warning;
        if (backgroundWarning != null) status.Text += "\n" + backgroundWarning;
        if (!testing && ready && tray?.Registered != true) status.Text += "\nトレイ登録に失敗しました。アプリを再起動してください。";
        status.ToolTip = Snapshot == null ? "未取得の値はバーを表示しません。" : "最終取得: " + Snapshot.FetchedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss zzz");
    }
    void UsageRow(string label, UsageWindow? value, Brush color)
    {
        bool remaining = label == "5h" ? Preferences.FiveHourRemaining : Preferences.WeekRemaining;
        double amount = value == null ? 0 : remaining ? Math.Clamp(100 - value.Used, 0, 100) : value.Used;
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        var heading = new Grid();
        heading.Children.Add(new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = value == null ? "取得不可" : amount.ToString("0.#") + (remaining ? "% 残り" : "% 使用"), HorizontalAlignment = HorizontalAlignment.Right, Foreground = value == null ? Muted : White, FontSize = 14 });
        var clickableContent = new StackPanel(); clickableContent.Children.Add(heading);
        var track = new Grid { Height = 7, Margin = new Thickness(0, 9, 0, 0), ClipToBounds = true };
        track.Children.Add(new Border { Background = ColorResource("WidgetTrack"), CornerRadius = new CornerRadius(4) });
        if (value != null)
        {
            var used = Math.Clamp(amount, 0, 100);
            var bar = new Grid(); bar.ColumnDefinitions.Add(new() { Width = new GridLength(used, GridUnitType.Star) }); bar.ColumnDefinitions.Add(new() { Width = new GridLength(100 - used, GridUnitType.Star) });
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = value.Used >= 90 ? ColorResource("WidgetWarning") : color }); track.Children.Add(bar);
        }
        clickableContent.Children.Add(track);
        var toggle = new Button { Content = clickableContent, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 2, 0, 6), HorizontalContentAlignment = HorizontalAlignment.Stretch, Cursor = Cursors.Hand,
            ToolTip = "左クリックで使用率／残量を切替（このバーのみ）" };
        toggle.SetResourceReference(Control.ForegroundProperty, "WidgetText");
        var container = new FrameworkElementFactory(typeof(Border)); container.SetValue(Border.BackgroundProperty, Brushes.Transparent); container.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); container.AppendChild(presenter);
        toggle.Template = new ControlTemplate(typeof(Button)) { VisualTree = container };
        System.Windows.Automation.AutomationProperties.SetName(toggle, label + " " + (remaining ? "残量" : "使用率") + "（クリックで切替）");
        toggle.Click += (_, e) => { e.Handled = true; if (label == "5h") Preferences.FiveHourRemaining = !Preferences.FiveHourRemaining; else Preferences.WeekRemaining = !Preferences.WeekRemaining; Persist(); Render(); };
        UsageButtons[label] = toggle; section.Children.Add(toggle);
        if (Preferences.ShowReset) section.Children.Add(ResetText(label, value));
        rows.Children.Add(section);
    }
    TextBlock ResetText(string label, UsageWindow? value) => new() { Text = value?.Reset is long epoch ? $"Reset  {DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime():MM/dd HH:mm}" + (epoch <= DateTimeOffset.Now.ToUnixTimeSeconds() ? " · 更新待ち" : "") : "Reset  取得不可", Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), ToolTip = label + " / Windowsの現地時刻（" + TimeZoneInfo.Local.DisplayName + "）" };
    void ResetRow(string label, UsageWindow? value) { var text = ResetText(label, value); text.Text = label + "  ·  " + text.Text; text.Margin = new Thickness(0, 0, 0, 10); rows.Children.Add(text); }
    void QueueSave() { if (ready && WindowState == WindowState.Normal) { saveTimer.Stop(); saveTimer.Start(); } }
    internal bool Persist()
    {
        if (WindowState == WindowState.Normal) { Preferences.Left = Left; Preferences.Top = Top; Preferences.Width = Width; Preferences.Height = Height; }
        var ok = store.Save(Preferences); if (ready && !closed) Render(); return ok;
    }
    void KeepOnScreen()
    {
        // Monitor work areas use physical pixels; the saved WPF bounds use DIPs.
        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(hwnd, 2); var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var work = new Rect(info.Work.Left / dpi.DpiScaleX, info.Work.Top / dpi.DpiScaleY, (info.Work.Right - info.Work.Left) / dpi.DpiScaleX, (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY);
        Width = Math.Min(Width, Math.Max(MinWidth, work.Width)); Height = Math.Min(Height, Math.Max(MinHeight, work.Height));
        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width)); Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
    }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
