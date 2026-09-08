namespace CodexUsageWidget;

public sealed record AlertStamp(long? Reset, bool Sent);
internal sealed record LowUsageAlert(string Key, double Remaining, long? Reset);

internal static class LowUsageAlerts
{
    // Only fresh successful samples reach this method. Missing/expired samples do not rearm alerts.
    internal static List<LowUsageAlert> Evaluate(Settings settings, UsageSnapshot snapshot, DateTimeOffset now)
    {
        var alerts = new List<LowUsageAlert>();
        foreach (var (key, value) in new[] { ("5h", snapshot.FiveHour), ("Week", snapshot.Week) })
        {
            if (value == null || !double.IsFinite(value.Used) || value.Used < 0 || value.Reset <= now.ToUnixTimeSeconds()) continue;
            settings.NotificationHistory.TryGetValue(key, out var previous);
            bool newWindow = value.Reset.HasValue && previous?.Reset != value.Reset;
            if (newWindow || previous == null)
                settings.NotificationHistory[key] = previous = new(value.Reset, false);
            if (value.Used <= 95)
            {
                // Recovery (including a redeemed reset) permits a future threshold crossing.
                settings.NotificationHistory[key] = new(value.Reset ?? previous.Reset, false);
                continue;
            }
            if (settings.LowUsageNotifications && !previous.Sent)
                alerts.Add(new(key, Math.Clamp(100 - value.Used, 0, 100), value.Reset));
        }
        return alerts;
    }
    internal static void MarkSent(Settings settings, IEnumerable<LowUsageAlert> alerts)
    { foreach (var alert in alerts) settings.NotificationHistory[alert.Key] = new(alert.Reset, true); }
    internal static string Message(IEnumerable<LowUsageAlert> alerts) => string.Join("\n", alerts.Select(a =>
        $"{a.Key}: 残り {a.Remaining:0.#}%" + (a.Reset is long reset ? $"（Reset {DateTimeOffset.FromUnixTimeSeconds(reset).ToLocalTime():MM/dd HH:mm}）" : "")));
}
