using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CodexUsageWidget;

public record UsageWindow(double Used, long? Reset);
public record UsageSnapshot(UsageWindow? FiveHour, UsageWindow? Week, string? Credits, DateTimeOffset FetchedAt);
public interface IUsageProvider { Task<UsageSnapshot> ReadAsync(CancellationToken token); }
public sealed class UsageException(string message) : Exception(message);

public static class UsageParser
{
    public static UsageSnapshot Parse(JsonElement root)
    {
        JsonElement bucket;
        if (root.TryGetProperty("rateLimitsByLimitId", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            if (!map.TryGetProperty("codex", out bucket))
                throw new UsageException("このアカウントにCodex利用枠がありません。");
        }
        else if (!root.TryGetProperty("rateLimits", out bucket) || bucket.ValueKind != JsonValueKind.Object)
            throw new UsageException("Codexから利用量が返されませんでした。");
        if (bucket.ValueKind != JsonValueKind.Object) throw new UsageException("Codex利用枠が未提供です。");
        if (bucket.TryGetProperty("limitId", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() != "codex")
            throw new UsageException("Codex以外の利用枠が返されました。");
        UsageWindow? Find(int minutes)
        {
            foreach (var key in new[] { "primary", "secondary" })
            {
                if (!bucket.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                if (!w.TryGetProperty("windowDurationMins", out var duration) || duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt64(out var mins) || mins != minutes) continue;
                if (!w.TryGetProperty("usedPercent", out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetDouble(out var value) || !double.IsFinite(value) || value < 0) continue;
                long? reset = null;
                if (w.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var epoch) && epoch >= 0 && epoch <= 253402300799) reset = epoch;
                return new(value, reset);
            }
            return null;
        }
        string? credits = null;
        if (bucket.TryGetProperty("credits", out var c) && c.ValueKind == JsonValueKind.Object)
        {
            if (c.TryGetProperty("unlimited", out var unlimited) && unlimited.ValueKind == JsonValueKind.True) credits = "無制限";
            else if (c.TryGetProperty("balance", out var balance) && balance.ValueKind == JsonValueKind.String &&
                decimal.TryParse(balance.GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 0)
                credits = n.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        }
        return new(Find(300), Find(10080), credits, DateTimeOffset.Now);
    }
}

public sealed class CodexProvider(Func<string?> configuredPath) : IUsageProvider
{
    public static string ResolveExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Path.IsPathFullyQualified(configured) && File.Exists(configured) && Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return configured;
            throw new UsageException("設定したCodex実行ファイルが見つかりません。右クリックから選び直してください。");
        }
        var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(bundled))
        {
            var found = Directory.EnumerateDirectories(bundled).Select(d => Path.Combine(d, "codex.exe"))
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (found != null) return found;
        }
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!Path.IsPathFullyQualified(entry)) continue;
            var file = Path.Combine(entry, "codex.exe");
            if (File.Exists(file)) return file;
        }
        throw new UsageException("Codexが見つかりません。Codexをインストールし、実行ファイルを選択してください。");
    }

    public async Task<UsageSnapshot> ReadAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var start = new ProcessStartInfo(ResolveExecutable(configuredPath()))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--stdio");
        using var process = new Process { StartInfo = start };
        bool started = false;
        try
        {
            process.Start(); started = true;
            // Drain stderr without storing it: Codex diagnostics can contain private context.
            var drain = DrainAsync(process.StandardError, ct);
            await SendAsync(process, new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "codex_usage_widget", version = "1.0.0" } } }, ct);
            await ResponseAsync(process, 1, ct);
            await SendAsync(process, new { method = "initialized" }, ct);
            await SendAsync(process, new { id = 2, method = "account/rateLimits/read" }, ct);
            var data = await ResponseAsync(process, 2, ct);
            return UsageParser.Parse(data);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new UsageException("更新がタイムアウトしました。接続を確認し、再更新してください。"); }
        catch (JsonException) { throw new UsageException("Codexの応答形式を読み取れません。Codexの更新状況を確認してください。"); }
        finally
        {
            if (started)
            {
                try { process.StandardInput.Close(); if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            }
        }
    }
    static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    { try { var buffer = new char[2048]; while (await reader.ReadAsync(buffer.AsMemory(), ct) > 0) { } } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { } }
    static Task SendAsync(Process p, object value, CancellationToken ct) => p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), ct);
    static async Task<JsonElement> ResponseAsync(Process p, int id, CancellationToken ct)
    {
        while (true)
        {
            var line = await p.StandardOutput.ReadLineAsync(ct);
            if (line == null) throw new UsageException("Codexへの接続が終了しました。Codexでログイン状態を確認してください。");
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var received) || received.ValueKind != JsonValueKind.Number || received.GetInt32() != id) continue;
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
                if (message.Contains("auth", StringComparison.OrdinalIgnoreCase) || message.Contains("login", StringComparison.OrdinalIgnoreCase))
                    throw new UsageException("Codexの認証が必要です。CodexでChatGPTアカウントにログインしてください。");
                throw new UsageException("Codexから利用量を取得できません。接続・ログイン・Codexのバージョンを確認してください。");
            }
            return root.GetProperty("result").Clone();
        }
    }
}
