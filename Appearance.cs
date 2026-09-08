using System.Security;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace CodexUsageWidget;

internal static class Appearance
{
    internal static bool IsLight(string mode, bool systemLight) => mode == "Light" || (mode == "System" && systemLight);
    internal static bool SystemIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or System.IO.IOException) { return false; }
    }
    internal static void Apply(ResourceDictionary resources, bool light)
    {
        var values = new Dictionary<string, string>
        {
            ["WidgetText"] = light ? "#17263B" : "#F3F6FC",
            ["WidgetMuted"] = light ? "#46566D" : "#B3C1D4",
            ["WidgetAccent"] = light ? "#087B61" : "#77E2C2",
            ["WidgetWeek"] = light ? "#5856B8" : "#AAB9FF",
            ["WidgetWarning"] = light ? "#A3441B" : "#FFAE8D",
            ["WidgetTrack"] = light ? "#291E3553" : "#28FFFFFF",
            ["WidgetMenu"] = light ? "#F5F7FB" : "#202C3D",
            ["WidgetBorder"] = light ? "#A7B4C5" : "#53627A",
            ["WidgetHover"] = light ? "#DEE6F1" : "#3D526E"
        };
        foreach (var (key, color) in values)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
            brush.Freeze(); resources[key] = brush;
        }
    }
}
