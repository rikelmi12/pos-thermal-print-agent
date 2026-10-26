using System.Text.Json;
using System.Text.Json.Serialization;

namespace CardapioPrintAgent;

internal sealed class AgentConfig
{
    public string ServerUrl { get; set; } = "http://localhost:8000";
    public string Token { get; set; } = string.Empty;
    public string PrinterName { get; set; } = string.Empty;
    public string ClientId { get; set; } = $"{Environment.MachineName}-print-agent";
    public int PollMs { get; set; } = 2500;
    public int ErrorPollMs { get; set; } = 10000;
    public int RequestTimeoutMs { get; set; } = 30000;
    public int PrintTimeoutMs { get; set; } = 30000;
    public string PrintMode { get; set; } = "thermal";
    public int PaperWidthMm { get; set; } = 58;
    public int FeedAfterPrintMm { get; set; } = 15;
    public bool StartWithWindows { get; set; } = true;

    [JsonIgnore]
    public int ReceiptColumns => GetReceiptColumns(PrintMode, PaperWidthMm);

    [JsonIgnore]
    public string PaperProfileLabel => NormalizePrintMode(PrintMode, PaperWidthMm) == "a4"
        ? "A4 / impressora comum"
        : $"Termica {NormalizePaperWidth(PrintMode, PaperWidthMm)}mm";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static AgentConfig Load()
    {
        AppPaths.EnsureDirectories();
        if (!File.Exists(AppPaths.ConfigPath)) return new AgentConfig();

        try
        {
            var raw = File.ReadAllText(AppPaths.ConfigPath);
            var parsed = JsonSerializer.Deserialize<AgentConfig>(raw, JsonOptions) ?? new AgentConfig();
            parsed.Normalize();
            return parsed;
        }
        catch
        {
            return new AgentConfig();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Normalize();
        var payload = JsonSerializer.Serialize(this, JsonOptions) + "\n";
        await AppPaths.WriteTextAtomicAsync(AppPaths.ConfigPath, payload, cancellationToken);
    }

    public void Normalize()
    {
        ServerUrl = (ServerUrl ?? string.Empty).Trim().TrimEnd('/');
        Token = (Token ?? string.Empty).Trim();
        PrinterName = (PrinterName ?? string.Empty).Trim();
        ClientId = string.IsNullOrWhiteSpace(ClientId)
            ? $"{Environment.MachineName}-print-agent"
            : ClientId.Trim();

        PollMs = ClampInt(PollMs, 500, 120000, 2500);
        ErrorPollMs = ClampInt(ErrorPollMs, 1000, 300000, 10000);
        RequestTimeoutMs = ClampInt(RequestTimeoutMs, 5000, 300000, 30000);
        PrintTimeoutMs = ClampInt(PrintTimeoutMs, 5000, 300000, 30000);
        PrintMode = NormalizePrintMode(PrintMode, PaperWidthMm);
        PaperWidthMm = NormalizePaperWidth(PrintMode, PaperWidthMm);
        FeedAfterPrintMm = ClampInt(FeedAfterPrintMm, 0, 80, 15);
    }

    public static string NormalizePrintMode(string? printMode, int paperWidthMm)
    {
        var normalized = (printMode ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is "a4" or "laser" or "standard") return "a4";
        if (normalized is "thermal" or "termica" or "receipt") return "thermal";
        return paperWidthMm >= 140 ? "a4" : "thermal";
    }

    public static int NormalizePaperWidth(string? printMode, int paperWidthMm)
    {
        var mode = NormalizePrintMode(printMode, paperWidthMm);
        if (mode == "a4") return 210;
        if (paperWidthMm >= 40 && paperWidthMm <= 120) return paperWidthMm;
        return paperWidthMm >= 75 ? 80 : 58;
    }

    public static int GetReceiptColumns(string? printMode, int paperWidthMm)
    {
        var mode = NormalizePrintMode(printMode, paperWidthMm);
        if (mode == "a4") return 64;

        var width = NormalizePaperWidth(mode, paperWidthMm);
        if (width <= 60) return 32;
        if (width < 75)
        {
            return Math.Clamp((int)Math.Round((width - 10) / 1.55), 32, 40);
        }

        return Math.Clamp((int)Math.Round((width - 8) / 1.70), 42, 80);
    }

    private static int ClampInt(int value, int min, int max, int fallback)
    {
        if (value < min || value > max) return fallback;
        return value;
    }
}
