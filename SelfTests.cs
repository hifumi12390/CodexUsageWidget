using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexUsageWidget;
internal static class SelfTests
{
    sealed class FakeProvider : IUsageProvider
    {
        public bool Fail;
        public int Calls;
        public Task<UsageSnapshot> ReadAsync(CancellationToken token)
        { Calls++; return Fail ? Task.FromException<UsageSnapshot>(new UsageException("テスト: 接続失敗")) : Task.FromResult(Parse("""{"rateLimits":{"primary":{"usedPercent":26,"windowDurationMins":300,"resetsAt":1893456000},"secondary":{"usedPercent":62,"windowDurationMins":10080,"resetsAt":1893801600},"credits":{"balance":"123.45","hasCredits":true,"unlimited":false}}}""")); }
    }
    static UsageSnapshot Parse(string json) { using var doc = JsonDocument.Parse(json); return UsageParser.Parse(doc.RootElement); }
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var results = new List<string>(); int exit = 0;
        void Check(bool pass, string name) { if (!pass) throw new Exception(name); results.Add("PASS " + name); }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Program.ApplyTheme(app);
        app.Startup += async (_, _) =>
        {
            WidgetWindow? window = null;
            try
            {
                var store = new SettingsStore(Path.Combine(output, "profile-" + Guid.NewGuid().ToString("N")));
                var defaults = store.Load();
                Check(defaults.ShowFiveHour && defaults.ShowWeek && defaults.ShowReset && !defaults.ShowCredits && !defaults.AlwaysOnTop, "first-run defaults");
                var absent = Parse("""{"rateLimits":{}}"""); Check(absent.FiveHour == null && absent.Week == null && absent.Credits == null, "missing data never becomes zero");
                var swapped = Parse("""{"rateLimits":{"primary":{"usedPercent":42,"windowDurationMins":10080},"secondary":{"usedPercent":12,"windowDurationMins":300}}}""");
                Check(swapped.FiveHour?.Used == 12 && swapped.Week?.Used == 42, "windows selected by duration rather than order");
                var mapped = Parse("""{"rateLimits":{"primary":{"usedPercent":99,"windowDurationMins":300}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":7,"windowDurationMins":300}}}}""");
                Check(mapped.FiveHour?.Used == 7, "authoritative multi-bucket mapping");
                var odd = Parse("""{"rateLimits":{"primary":{"usedPercent":5,"windowDurationMins":60},"credits":{"hasCredits":false,"unlimited":false,"balance":null}}}""");
                Check(odd.FiveHour == null && odd.Credits == null, "unknown duration and null balance remain unavailable");
                var nullDuration = Parse("""{"rateLimits":{"primary":{"usedPercent":null,"windowDurationMins":null}}}"""); Check(nullDuration.FiveHour == null, "nullable protocol fields accepted");
                var zero = Parse("""{"rateLimits":{"credits":{"balance":"0","unlimited":false}}}"""); Check(zero.Credits == "0", "actual zero credit retained");
                var fake = new FakeProvider(); window = new WidgetWindow(store, fake, true); window.Show();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle != IntPtr.Zero, "native WPF window starts");
                Check(!window.ShowInTaskbar && defaults.GlassEnabled && defaults.LowUsageNotifications, "tray-only window and new defaults");
                using (var tray = new TrayIcon(System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(window).Handle)!, () => { }, () => { }))
                { Check(tray.Registered, "Windows Shell accepts tray registration"); tray.Dispose(); Check(!tray.Registered, "tray icon removed on dispose"); }
                window.HideToTray(); Check(!window.IsVisible, "hide retains running window");
                window.RestoreFromTray(); Check(window.IsVisible && !window.ShowInTaskbar, "restore remains absent from taskbar");
                window.SetGlass(false);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(!window.Preferences.GlassEnabled && window.Background is SolidColorBrush solid && solid.Color.A == 255 && WindowBackdrop.ReadType(window) is null or 1, "glass OFF uses opaque background and disables DWM backdrop");
                window.SetGlass(true);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(window.BackdropState is { RequestedType: 3 } && (window.BackdropState.GlassActive ? WindowBackdrop.ReadType(window) == 3 : window.Background is SolidColorBrush), "glass ON reapplied after initialization with supported fallback");
                results.Add("INFO DWM glass accepted: " + window.BackdropState!.GlassActive);
                var now = DateTimeOffset.Now;
                UsageSnapshot Quota(double used, long? reset = null) => new(new(used, reset ?? now.AddHours(2).ToUnixTimeSeconds()), new(used, reset ?? now.AddDays(2).ToUnixTimeSeconds()), null, now);
                var alertSettings = new Settings();
                Check(LowUsageAlerts.Evaluate(alertSettings, Quota(95), now).Count == 0, "exactly 5 percent does not notify");
                var low = Quota(96); var alerts = LowUsageAlerts.Evaluate(alertSettings, low, now);
                Check(alerts.Count == 2 && alerts.All(a => a.Remaining == 4), "both quotas below 5 percent detected");
                LowUsageAlerts.MarkSent(alertSettings, alerts);
                Check(LowUsageAlerts.Evaluate(alertSettings, low, now).Count == 0, "repeated refresh suppresses duplicate notification");
                Check(LowUsageAlerts.Evaluate(alertSettings, Quota(99, now.AddDays(3).ToUnixTimeSeconds()), now).Count == 2, "new quota window rearms alerts");
                LowUsageAlerts.Evaluate(alertSettings, Quota(20), now);
                Check(LowUsageAlerts.Evaluate(alertSettings, low, now).Count == 2, "recovery rearms next threshold crossing");
                alertSettings.LowUsageNotifications = false;
                Check(LowUsageAlerts.Evaluate(alertSettings, low, now).Count == 0, "notification OFF suppresses alerts");
                Check(LowUsageAlerts.Evaluate(new(), Quota(99, now.AddSeconds(-1).ToUnixTimeSeconds()), now).Count == 0 && LowUsageAlerts.Evaluate(new(), new(null, null, null, now), now).Count == 0, "expired and unavailable samples never notify");
                int notices = 0;
                window.NotificationSink = (_, message) => { notices++; return message.Contains("5h") && message.Contains("Week"); };
                window.ProcessAlerts(low); window.ProcessAlerts(low);
                Check(notices == 1, "simultaneous low quotas send one combined notification");
                var storedAlerts = store.Load();
                Check(LowUsageAlerts.Evaluate(storedAlerts, low, now).Count == 0, "delivered notification history survives settings reload");
                window.Toggles["Notifications"].IsChecked = false;
                window.Toggles["Notifications"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(!window.Preferences.LowUsageNotifications, "notification menu changes preference");
                await window.RefreshAsync(); Check(window.Snapshot?.FiveHour?.Used == 26 && window.Snapshot.Week?.Used == 62, "5h / Week rendered from provider");
                Check(Text(window).Contains("26% 使用") && Text(window).Contains("62% 使用"), "percentage text and bars bound");
                Check(((SolidColorBrush)window.UsageButtons["5h"].Foreground).Color == ((SolidColorBrush)Application.Current.Resources["WidgetText"]).Color, "dark bar labels use readable theme foreground");
                Check(defaults.Theme == "Dark" && !defaults.FiveHourRemaining && !defaults.WeekRemaining, "v1 settings migrate to dark/used defaults");
                var callsBeforeToggle = fake.Calls;
                window.UsageButtons["5h"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Text(window).Contains("74% 残り") && Text(window).Contains("62% 使用") && window.Preferences.FiveHourRemaining && !window.Preferences.WeekRemaining, "5h click toggles only its text and remaining mode");
                Check(fake.Calls == callsBeforeToggle, "bar toggle does not query network");
                window.UsageButtons["5h"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Text(window).Contains("26% 使用"), "second click restores used mode");
                window.ThemeItems["Light"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(window.LightTheme && window.Preferences.Theme == "Light" && window.ThemeItems["Light"].IsChecked, "light menu applies and selects theme");
                Capture(window, Path.Combine(output, "light.png"));
                window.ThemeItems["System"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(window.LightTheme == Appearance.SystemIsLight() && window.Preferences.Theme == "System", "system theme reads Windows app preference");
                Check(Appearance.IsLight("System", true) && !Appearance.IsLight("System", false) && Appearance.IsLight("Light", false) && !Appearance.IsLight("Dark", true), "system light/dark and explicit theme precedence");
                window.SetTheme("Dark");
                Capture(window, Path.Combine(output, "default.png"));
                window.ContextMenu.PlacementTarget = window; window.ContextMenu.IsOpen = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(window.ContextMenu.IsOpen && window.ContextMenu.Items.Count >= 12, "context menu opens"); window.ContextMenu.IsOpen = false;
                foreach (var key in new[] { "5h", "Week", "Reset", "Credits", "Topmost" })
                {
                    var item = window.Toggles[key]; bool original = item.IsChecked; item.IsChecked = !original; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Check(item.IsChecked != original, key + " menu toggle");
                }
                Check(!window.Preferences.ShowFiveHour && !window.Preferences.ShowWeek && !window.Preferences.ShowReset && window.Preferences.ShowCredits && window.Topmost, "toggle state reaches preferences and topmost");
                Check(Text(window).Contains("123.45") && !Text(window).Contains("26% 使用"), "visibility and Credits presentation");
                foreach (var key in new[] { "5h", "Week", "Reset" }) { window.Toggles[key].IsChecked = true; window.Toggles[key].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
                var count = fake.Calls; ((MenuItem)window.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(fake.Calls == count + 1, "manual refresh menu invokes provider");
                var previous = window.Snapshot; fake.Fail = true; await window.RefreshAsync();
                Check(window.Snapshot == previous && !window.Busy && Text(window).Contains("前回値"), "refresh failure preserves last value and recovers UI");
                Capture(window, Path.Combine(output, "failure.png")); fake.Fail = false; await window.RefreshAsync();
                Check(!Text(window).Contains("前回値"), "successful retry clears stale error");
                window.Width = 280; window.Height = 180; window.UpdateLayout();
                Check(window.ActualWidth == 280 && window.ActualHeight == 180, "minimum resize and scroll layout"); Capture(window, Path.Combine(output, "minimum.png"));
                window.Width = 520; window.Height = 390; window.Left += 20; window.Top += 30;
                Check(window.Persist(), "settings atomic save");
                var source = Path.Combine(output, "test-background.png");
                var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawRectangle(new LinearGradientBrush(Colors.DarkSlateBlue, Colors.Teal, 45), null, new Rect(0, 0, 600, 400));
                var bitmap = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); WritePng(bitmap, source);
                window.SelectImageFile(source); File.Delete(source);
                Check(window.Preferences.ImageBackground && File.Exists(window.Preferences.BackgroundImage), "background imported independently of original");
                Check(WindowBackdrop.ReadType(window) is null or 1, "image mode disables glass backdrop");
                window.SetGlass(false);
                Check(!window.Preferences.ImageBackground && File.Exists(window.Preferences.BackgroundImage), "glass OFF retains imported image");
                var restoreImage = window.ContextMenu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "前回の背景画像を使用");
                restoreImage.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(window.Preferences.ImageBackground, "background image restored through menu");
                Capture(window, Path.Combine(output, "image.png"));
                window.SetTheme("Light"); window.UsageButtons["Week"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Text(window).Contains("38% 残り") && Text(window).Contains("26% 使用"), "Week toggles independently");
                Capture(window, Path.Combine(output, "light-image.png"));
                var savedLeft = window.Left; var savedTop = window.Top; var savedImage = window.Preferences.BackgroundImage;
                window.Close(); window = new WidgetWindow(store, fake, true); window.Show();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(window.Width == 520 && window.Height == 390 && Math.Abs(window.Left - savedLeft) < 2 && Math.Abs(window.Top - savedTop) < 2, "close/reopen restores window geometry");
                Check(window.Topmost && window.Preferences.ShowCredits && window.Preferences.ImageBackground && window.Preferences.BackgroundImage == savedImage, "close/reopen restores toggles/topmost/background");
                Check(window.LightTheme && window.Preferences.Theme == "Light" && window.Preferences.WeekRemaining && !window.Preferences.FiveHourRemaining, "theme and per-bar mode survive restart");
                Check(!window.Preferences.GlassEnabled && !window.Preferences.LowUsageNotifications, "glass and notification settings survive restart");
                await window.RefreshAsync(); Capture(window, Path.Combine(output, "restored.png"));
                window.Close(); window = null;
                var saved = store.Load(); Check(store.Save(saved), "settings backup write"); File.WriteAllText(store.FilePath, "{broken");
                var recovered = store.Load(); Check(recovered.Width == 520 && store.Warning != null, "corrupt settings recover from backup");
                var invalid = new Settings { Width = double.NaN, Height = -100, Left = double.PositiveInfinity }; invalid.Normalize();
                Check(invalid.Width == 340 && invalid.Height == 180 && invalid.Left == 80, "invalid geometry normalized");
                var blocked = Path.Combine(output, "not-a-directory"); File.WriteAllText(blocked, "test");
                var badStore = new SettingsStore(blocked); Check(!badStore.Save(new()) && badStore.Warning != null, "save failure handled without crash");
            }
            catch (Exception e) { results.Add("FAIL " + e); exit = 1; }
            finally { window?.Close(); File.WriteAllLines(Path.Combine(output, "results.txt"), results); app.Shutdown(exit); }
        };
        app.Run(); return exit;
    }
    static string Text(DependencyObject root)
    {
        if (root is Window window) window.UpdateLayout();
        var s = root is TextBlock t ? t.Text + "\n" : "";
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) s += Text(VisualTreeHelper.GetChild(root, i));
        return s;
    }
    static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window); WritePng(image, path);
    }
    static void WritePng(BitmapSource image, string path) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
}
