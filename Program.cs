using System.IO;
using System.Text.Json;
using System.Windows;

namespace CodexUsageWidget;
internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--probe")
        {
            try
            {
                var snapshot = new CodexProvider(() => null).ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllText(args[1], JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true })); return 0;
            }
            catch (Exception e) { File.WriteAllText(args[1], e is UsageException ? e.Message : "Probe failed: " + e.GetType().Name); return 1; }
        }
        if (args.Length == 2 && args[0] == "--self-test") return SelfTests.Run(Path.GetFullPath(args[1]));
        using var mutex = new Mutex(true, @"Local\CodexUsageWidget-" + Environment.UserName, out bool first);
        if (!first) { MessageBox.Show("Codex Usage Widget は既に起動しています。タスクトレイのアイコンから表示してください。旧版から更新する場合は旧版を終了してください。", "Codex Usage Widget"); return 0; }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        ApplyTheme(app);
        try { var window = new WidgetWindow(new SettingsStore(SettingsStore.DefaultDirectory)); app.SessionEnding += (_, _) => window.Persist(); return app.Run(window); }
        catch (Exception) { MessageBox.Show("起動できませんでした。設定フォルダーのアクセス権を確認してください。\n" + SettingsStore.DefaultDirectory, "Codex Usage Widget", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
    }
    internal static void ApplyTheme(Application app) => app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/CodexUsageWidget;component/Theme.xaml", UriKind.Relative) });
}
