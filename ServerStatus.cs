using System.Net.Http;
using System.Text.Json;

namespace CodexUsageWidget;

public sealed record ServiceHealth(string Name, string State)
{
    public bool IsIncident => State is "degraded_performance" or "partial_outage" or "major_outage";
    public string Label => State switch
    {
        "operational" => "正常稼働", "degraded_performance" => "性能低下",
        "partial_outage" => "一部障害", "major_outage" => "停止中",
        "under_maintenance" => "メンテナンス", _ => "状態不明"
    };
}
public sealed record ServerSnapshot(string Indicator, IReadOnlyList<ServiceHealth> Services, DateTimeOffset FetchedAt)
{
    // Generic API components cannot reliably be assigned to ChatGPT. Keep explicitly
    // named product components and the shared login service; the official link covers the rest.
    public ServerSnapshot ForWidget() => new("none", Services.Where(s =>
        s.Name.Contains("Codex", StringComparison.OrdinalIgnoreCase) ||
        s.Name.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
        s.Name.Equals("Login", StringComparison.OrdinalIgnoreCase) ||
        s.Name.Equals("VS Code extension", StringComparison.OrdinalIgnoreCase)).ToList(), FetchedAt);
    public bool HasIncident => Indicator is "minor" or "major" or "critical" || Services.Any(s => s.IsIncident);
    public string Label => HasIncident ? "障害あり" : Indicator == "maintenance" || Services.Any(s => s.State == "under_maintenance")
        ? "メンテナンス" : Indicator == "none" && Services.Count > 0 && Services.All(s => s.State == "operational") ? "正常稼働" : "状態不明";
}
public interface IServerStatusProvider { Task<ServerSnapshot> ReadAsync(CancellationToken token); }
public sealed class OpenAiStatusProvider : IServerStatusProvider
{
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    public async Task<ServerSnapshot> ReadAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://status.openai.com/api/v2/summary.json");
        request.Headers.UserAgent.ParseAdd("CodexUsageWidget/1.3.0");
        request.Headers.CacheControl = new() { NoCache = true };
        using var response = await Client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        return Parse(json.RootElement);
    }
    internal static ServerSnapshot Parse(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Array)
            throw new JsonException("Status response missing required fields");
        static string Read(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        var services = new List<ServiceHealth>();
        foreach (var component in components.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Object) continue;
            var name = Read(component, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            services.Add(new(name.Length > 160 ? name[..160] : name, Read(component, "status")));
        }
        return new(Read(status, "indicator"), services.OrderByDescending(s => s.IsIncident)
            .ThenByDescending(s => s.Name.Contains("Codex", StringComparison.OrdinalIgnoreCase)).ThenBy(s => s.Name).ToList(), DateTimeOffset.Now);
    }
}
