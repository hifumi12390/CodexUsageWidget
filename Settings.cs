using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace CodexUsageWidget;
public sealed class Settings
{
    public int Version { get; set; } = 1;
    public double Left { get; set; } = 80;
    public double Top { get; set; } = 80;
    public double Width { get; set; } = 340;
    public double Height { get; set; } = 290;
    public bool ShowFiveHour { get; set; } = true;
    public bool ShowWeek { get; set; } = true;
    public bool ShowReset { get; set; } = true;
    public bool ShowCredits { get; set; }
    public bool AlwaysOnTop { get; set; }
    public string Theme { get; set; } = "Dark";
    public bool FiveHourRemaining { get; set; }
    public bool WeekRemaining { get; set; }
    public bool ImageBackground { get; set; }
    public bool GlassEnabled { get; set; } = true;
    public bool LowUsageNotifications { get; set; } = true;
    public Dictionary<string, AlertStamp> NotificationHistory { get; set; } = new();
    public string? BackgroundImage { get; set; }
    public string? CodexPath { get; set; }
    public void Normalize()
    {
        NotificationHistory ??= new();
        if (Theme is not ("Dark" or "Light" or "System")) Theme = "Dark";
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 280, 800) : 340;
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 180, 900) : 290;
        if (!double.IsFinite(Left)) Left = 80;
        if (!double.IsFinite(Top)) Top = 80;
    }
}
public sealed class SettingsStore(string directory)
{
    public string DirectoryPath { get; } = directory;
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public string? Warning { get; private set; }
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageWidget");
    public Settings Load()
    {
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? throw new JsonException();
                s.Normalize();
                if (path.EndsWith(".bak")) Warning = "設定のバックアップを復元しました。";
                return s;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            { Warning = "設定を読み込めませんでした。初期設定またはバックアップを使用しています。"; }
        }
        return new();
    }
    public bool Save(Settings settings)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var temp = FilePath + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, settings, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
            else File.Move(temp, FilePath);
            Warning = null; return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Warning = "設定を保存できません。保存先の空き容量・アクセス権を確認してください。"; return false; }
    }
    public static BitmapImage LoadImage(string file)
    {
        var info = new FileInfo(file);
        if (info.Length > 30 * 1024 * 1024) throw new IOException("画像は30MB以下を選択してください。");
        using var input = File.OpenRead(file);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 1600; image.StreamSource = input; image.EndInit(); image.Freeze(); return image;
    }
    public string ImportImage(string source)
    {
        var image = LoadImage(source);
        Directory.CreateDirectory(DirectoryPath);
        var destination = Path.Combine(DirectoryPath, "background-" + Guid.NewGuid().ToString("N") + ".png");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(destination)) encoder.Save(stream);
        return destination;
    }
}
