namespace CodexUsageWidget;

internal static class RefreshPolicy
{
    internal static readonly int[] UsageChoices = [15, 30, 60, 300, 900];
    internal static readonly int[] ServerChoices = [60, 300, 900];
    internal static string Label(int seconds) => seconds < 60 ? $"{seconds}秒" : $"{seconds / 60}分";
    internal static TimeSpan Delay(int seconds, int failures) => failures == 0
        ? TimeSpan.FromSeconds(seconds)
        : TimeSpan.FromSeconds(Math.Min(1800, Math.Max(60, seconds) * Math.Pow(2, Math.Min(5, failures))));
}
