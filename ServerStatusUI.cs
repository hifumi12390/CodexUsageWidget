using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexUsageWidget;

public sealed partial class WidgetWindow
{
    readonly IServerStatusProvider serverProvider;
    readonly DispatcherTimer serverTimer = new();
    internal readonly Expander ServerExpander = new();
    readonly TextBlock serverHeader = new();
    readonly StackPanel serverRows = new();
    internal readonly Dictionary<int, MenuItem> RefreshItems = new();
    internal readonly Dictionary<int, MenuItem> ServerRefreshItems = new();
    internal ServerSnapshot? ServerSnapshot;
    internal bool ServerBusy { get; private set; }
    string? serverFailure;
    int serverFailures;
    DateTimeOffset lastServerAttempt = DateTimeOffset.MinValue;
    internal readonly HealthHistory ServerHistory = new();
    bool serverResizeQueued;
    double? automaticServerHeight;
    internal void RememberServerSize()
    {
        if (!ready) return;
        if (!ServerExpander.IsExpanded) Preferences.CollapsedHeight = Height;
        else if (automaticServerHeight is not double automatic || Math.Abs(Height - automatic) > .5)
            Preferences.ServerExpandedHeight = Height;
    }
    internal void InitializeServerSection(StackPanel parent)
    {
        ServerExpander.Header = serverHeader; ServerExpander.Content = serverRows;
        ServerExpander.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ServerExpander.Margin = new Thickness(0, 4, 0, 0);
        ServerExpander.IsExpanded = Preferences.ServerStatusExpanded;
        ServerExpander.SetResourceReference(Control.ForegroundProperty, "WidgetText");
        serverHeader.FontWeight = FontWeights.SemiBold;
        serverRows.Margin = new Thickness(4, 8, 0, 0);
        ServerExpander.Expanded += (_, _) => { Preferences.CollapsedHeight = Height; Preferences.ServerStatusExpanded = true; QueueServerResize(); QueueSave(); };
        ServerExpander.Collapsed += (_, _) => { Preferences.ServerExpandedHeight = Height; Preferences.ServerStatusExpanded = false; Height = Preferences.CollapsedHeight ?? Height; QueueSave(); };
        parent.Children.Add(ServerExpander);
        serverTimer.Tick += async (_, _) => await RefreshServerAsync();
    }
    void AddRefreshSettings()
    {
        var usage = new MenuItem { Header = "利用量の更新間隔", ToolTip = "短い間隔で最新値を再取得します。サーバー側の反映には遅延があります。" };
        foreach (int seconds in RefreshPolicy.UsageChoices)
        {
            var item = Item(RefreshPolicy.Label(seconds) + (seconds == 15 ? "（高速更新）" : ""), () => SetRefreshSeconds(seconds));
            item.IsCheckable = true; item.IsChecked = seconds == Preferences.RefreshSeconds;
            RefreshItems[seconds] = item; usage.Items.Add(item);
        }
        ContextMenu.Items.Add(usage);
        var server = new MenuItem { Header = "サーバー情報の更新間隔" };
        foreach (int seconds in RefreshPolicy.ServerChoices)
        {
            var item = Item(RefreshPolicy.Label(seconds), () => SetServerRefreshSeconds(seconds));
            item.IsCheckable = true; item.IsChecked = seconds == Preferences.ServerRefreshSeconds;
            ServerRefreshItems[seconds] = item; server.Items.Add(item);
        }
        ContextMenu.Items.Add(server);
    }
    internal void SetRefreshSeconds(int seconds)
    {
        if (!RefreshPolicy.UsageChoices.Contains(seconds)) return;
        Preferences.RefreshSeconds = seconds;
        foreach (var (value, item) in RefreshItems) item.IsChecked = value == seconds;
        refreshTimer.Stop(); refreshTimer.Interval = RefreshPolicy.Delay(seconds, failures);
        nextRefresh = DateTimeOffset.Now + refreshTimer.Interval;
        if (!testing && !closed && !Busy) refreshTimer.Start();
        Persist();
    }
    internal void SetServerRefreshSeconds(int seconds)
    {
        if (!RefreshPolicy.ServerChoices.Contains(seconds)) return;
        Preferences.ServerRefreshSeconds = seconds;
        foreach (var (value, item) in ServerRefreshItems) item.IsChecked = value == seconds;
        serverTimer.Stop(); serverTimer.Interval = RefreshPolicy.Delay(seconds, serverFailures);
        if (!testing && !closed && !ServerBusy) serverTimer.Start();
        Persist();
    }
    internal async Task RefreshServerAsync(bool manual = true)
    {
        // Usage polling can run more often; do not let it bypass the status service's own schedule.
        if (ServerBusy || closed || (!testing && DateTimeOffset.Now - lastServerAttempt <
            (manual ? TimeSpan.FromSeconds(10) : RefreshPolicy.Delay(Preferences.ServerRefreshSeconds, serverFailures)))) return;
        serverTimer.Stop(); lastServerAttempt = DateTimeOffset.Now; ServerBusy = true; RenderServerStatus();
        try { ServerSnapshot = await serverProvider.ReadAsync(lifetime.Token); ServerHistory.Append(ServerSnapshot, DateTimeOffset.Now); serverFailure = null; serverFailures = 0; }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception) { serverFailure = "稼働状況を取得できません"; ServerHistory.Append(null, DateTimeOffset.Now); serverFailures++; }
        finally
        {
            ServerBusy = false;
            if (!closed)
            {
                serverTimer.Interval = RefreshPolicy.Delay(Preferences.ServerRefreshSeconds, serverFailures);
                if (!testing) serverTimer.Start();
                RenderServerStatus(true); QueueServerResize();
            }
        }
    }
    internal Brush ServerHealthBrush(bool incident) => incident ? new SolidColorBrush(LightTheme ? Color.FromRgb(178, 25, 40) : Color.FromRgb(255, 137, 148)) : White;
    internal void RenderServerStatus(bool animate = false)
    {
        var relevant = ServerSnapshot?.ForWidget();
        serverHeader.Text = "Codex / ChatGPT · " + (serverFailure != null ? "取得失敗" : relevant?.Label ?? (ServerBusy ? "確認中…" : "未取得"));
        serverHeader.Foreground = serverFailure != null ? Muted : ServerHealthBrush(relevant?.HasIncident == true);
        serverRows.Children.Clear();
        if (serverFailure != null) serverRows.Children.Add(new TextBlock { Text = serverFailure + (ServerSnapshot != null ? "（以下は前回値）" : "。停止しているかは不明です。"), Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        if (ServerSnapshot != null)
        {
            foreach (var service in relevant!.Services)
            {
                var card = new StackPanel { Margin = new Thickness(0, 0, 0, 10), ToolTip = ServerHistory.Summary(service.Name) };
                var grid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
                grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var foreground = ServerHealthBrush(service.IsIncident);
                grid.Children.Add(new TextBlock { Text = service.Name, Foreground = foreground, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0) });
                var state = new TextBlock { Text = service.Label, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(state, 1); grid.Children.Add(state); card.Children.Add(grid);
                card.Children.Add(new StatusTimeline(ServerHistory, service.Name, LightTheme, animate));
                serverRows.Children.Add(card);
            }
            if (relevant.Services.Count == 0) serverRows.Children.Add(new TextBlock { Text = "対象サービスの情報なし", Foreground = Muted });
            serverRows.Children.Add(new TextBlock { Text = "直近90回の観測 · 右端が最新", Foreground = Muted, FontSize = 10 });
            serverRows.Children.Add(new TextBlock { Text = $"確認 {ServerSnapshot.FetchedAt:MM/dd HH:mm:ss} · {RefreshPolicy.Label(Preferences.ServerRefreshSeconds)}間隔", Foreground = Muted, FontSize = 10 });
        }
        var link = new Hyperlink(new Run("公式の障害情報を開く ↗")) { Foreground = Muted, ToolTip = "https://status.openai.com/" };
        link.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("https://status.openai.com/") { UseShellExecute = true }); } catch { } };
        var linkText = new TextBlock { Margin = new Thickness(0, 8, 0, 4), FontSize = 11 }; linkText.Inlines.Add(link);
        serverRows.Children.Add(linkText);
    }
    internal void QueueServerResize()
    {
        if (closed || !ready || !ServerExpander.IsExpanded || serverResizeQueued) return;
        serverResizeQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            serverResizeQueued = false;
            if (closed || !ServerExpander.IsExpanded || WindowState != WindowState.Normal) return;
            Preferences.CollapsedHeight ??= Height;
            serverRows.Measure(new Size(Math.Max(180, Width - 60), double.PositiveInfinity));
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            double bottom = SystemParameters.WorkArea.Bottom;
            if (GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) bottom = info.Work.Bottom / VisualTreeHelper.GetDpi(this).DpiScaleY;
            automaticServerHeight = Preferences.ServerExpandedHeight is double remembered
                ? Math.Clamp(remembered, MinHeight, Math.Max(MinHeight, Math.Min(MaxHeight, bottom - Top)))
                : ExpandedHeight(Preferences.CollapsedHeight.Value, serverRows.DesiredSize.Height + 12, bottom - Top, MinHeight, MaxHeight);
            Height = automaticServerHeight.Value;
            QueueSave();
        }, DispatcherPriority.Loaded);
    }
    internal static double ExpandedHeight(double collapsed, double content, double room, double min, double max) => Math.Clamp(collapsed + content, min, Math.Max(min, Math.Min(max, room)));
}
