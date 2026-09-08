using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CodexUsageWidget;

internal sealed record HealthSample(DateTimeOffset Time, IReadOnlyDictionary<string, string> States);
internal sealed class HealthHistory
{
    internal const int Capacity = 90;
    internal List<HealthSample> Samples { get; } = new();
    internal void Append(ServerSnapshot? snapshot, DateTimeOffset time)
    {
        Samples.Add(new(time, snapshot?.Services.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First().State) ?? new Dictionary<string, string>()));
        if (Samples.Count > Capacity) Samples.RemoveAt(0);
    }
    internal static string State(HealthSample sample, string name) => sample.States.TryGetValue(name, out var state) ? state : "unknown";
    internal string Summary(string name)
    {
        var states = Samples.Select(s => State(s, name)).Where(s => s is "operational" or "degraded_performance" or "partial_outage" or "major_outage" or "under_maintenance").ToList();
        return states.Count == 0 ? "観測なし" : $"正常 {100.0 * states.Count(s => s == "operational") / states.Count:0.#}% · {states.Count}回観測";
    }
}

internal sealed class StatusTimeline : FrameworkElement
{
    readonly HealthHistory history;
    readonly string service;
    readonly bool light;
    internal StatusTimeline(HealthHistory history, string service, bool light, bool animate)
    {
        this.history = history; this.service = service; this.light = light;
        Height = 12; ClipToBounds = true; SnapsToDevicePixels = true;
        ToolTip = "灰色: 未観測／取得失敗 · 緑: 正常 · 橙: 性能低下／一部障害 · 赤: 停止 · 青: メンテナンス";
        if (animate)
        {
            var transform = new TranslateTransform(); RenderTransform = transform;
            Loaded += (_, _) => transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(Math.Max(3, ActualWidth / HealthHistory.Capacity), 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }
    internal static Color ColorFor(string state, bool light) => state switch
    {
        "operational" => Color.FromRgb(15, 163, 136),
        "degraded_performance" or "partial_outage" => Color.FromRgb(234, 147, 35),
        "major_outage" => light ? Color.FromRgb(192, 35, 57) : Color.FromRgb(255, 114, 132),
        "under_maintenance" => Color.FromRgb(78, 145, 218),
        _ => light ? Color.FromRgb(207, 216, 223) : Color.FromRgb(64, 79, 96)
    };
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double step = ActualWidth / HealthHistory.Capacity;
        int empty = HealthHistory.Capacity - history.Samples.Count;
        for (int i = 0; i < HealthHistory.Capacity; i++)
        {
            string state = i < empty ? "unknown" : HealthHistory.State(history.Samples[i - empty], service);
            dc.DrawRectangle(new SolidColorBrush(ColorFor(state, light)), null, new Rect(i * step, 0, Math.Max(.5, step * .45), ActualHeight));
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = (int)(e.GetPosition(this).X / Math.Max(1, ActualWidth) * HealthHistory.Capacity) - (HealthHistory.Capacity - history.Samples.Count);
        ToolTip = index >= 0 && index < history.Samples.Count ? $"{history.Samples[index].Time:MM/dd HH:mm:ss} · {new ServiceHealth(service, HealthHistory.State(history.Samples[index], service)).Label}" : "未観測（過去の稼働を推測しません）";
    }
}
