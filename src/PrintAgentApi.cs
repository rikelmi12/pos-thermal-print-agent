using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CardapioPrintAgent;

internal sealed class PrintAgentApi
{
    private readonly AgentConfig _config;
    private readonly AgentLogger _logger;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public PrintAgentApi(AgentConfig config, AgentLogger logger)
    {
        _config = config;
        _logger = logger;

        _http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<PrintJob?> ClaimNextJobAsync(CancellationToken cancellationToken)
    {
        var payload = new
        {
            clientId = _config.ClientId,
            printMode = _config.PrintMode,
            paperWidthMm = _config.PaperWidthMm,
            receiptColumns = _config.ReceiptColumns,
        };
        var result = await SendAsync(HttpMethod.Post, "/api/print-agent/jobs/claim", payload, cancellationToken);
        if (!result.Ok) throw new InvalidOperationException(result.ErrorMessage);
        return result.Job;
    }

    public async Task CompleteJobAsync(long jobId, CancellationToken cancellationToken)
    {
        var payload = new { clientId = _config.ClientId };
        var result = await SendAsync(HttpMethod.Post, $"/api/print-agent/jobs/{jobId}/complete", payload, cancellationToken);
        if (!result.Ok) throw new InvalidOperationException(result.ErrorMessage);
    }

    public async Task FailJobAsync(long jobId, string errorMessage, CancellationToken cancellationToken)
    {
        var payload = new
        {
            clientId = _config.ClientId,
            error = (errorMessage ?? string.Empty).Trim().Length > 900
                ? (errorMessage ?? string.Empty).Trim()[..900]
                : (errorMessage ?? string.Empty).Trim(),
        };

        var result = await SendAsync(HttpMethod.Post, $"/api/print-agent/jobs/{jobId}/fail", payload, cancellationToken);
        if (!result.Ok) throw new InvalidOperationException(result.ErrorMessage);
    }

    public async Task<PrintJobsStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        var result = await SendAsync(HttpMethod.Get, "/api/print-agent/jobs/stats", null, cancellationToken);
        if (!result.Ok) throw new InvalidOperationException(result.ErrorMessage);
        return result.Stats ?? new PrintJobsStats();
    }

    private async Task<ApiEnvelope> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_config.RequestTimeoutMs);

        var request = new HttpRequestMessage(method, BuildUrl(path));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.Token);
        request.Headers.Add("X-Print-Agent-Id", _config.ClientId);

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout HTTP apos {_config.RequestTimeoutMs}ms.");
        }

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var envelope = ParseEnvelope(raw);

        if (!response.IsSuccessStatusCode)
        {
            var detail = envelope.ErrorMessage;
            if (string.IsNullOrWhiteSpace(detail)) detail = $"HTTP {(int)response.StatusCode}";
            throw new InvalidOperationException(detail);
        }

        return envelope;
    }

    private ApiEnvelope ParseEnvelope(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new ApiEnvelope { Ok = true };

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            var envelope = new ApiEnvelope
            {
                Ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True,
                ErrorMessage = root.TryGetProperty("error", out var errEl) ? errEl.GetString() ?? string.Empty : string.Empty,
            };

            if (root.TryGetProperty("job", out var jobEl) && jobEl.ValueKind == JsonValueKind.Object)
            {
                envelope.Job = new PrintJob
                {
                    Id = jobEl.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : 0,
                    OrderCode = jobEl.TryGetProperty("orderCode", out var codeEl) ? codeEl.GetString() ?? string.Empty : string.Empty,
                    Text = jobEl.TryGetProperty("text", out var txtEl) ? txtEl.GetString() ?? string.Empty : string.Empty,
                    Attempts = jobEl.TryGetProperty("attempts", out var attEl) ? attEl.GetInt32() : 0,
                    ClaimedBy = jobEl.TryGetProperty("claimedBy", out var cbEl) ? cbEl.GetString() ?? string.Empty : string.Empty,
                };
            }

            if (root.TryGetProperty("stats", out var statsEl) && statsEl.ValueKind == JsonValueKind.Object)
            {
                envelope.Stats = new PrintJobsStats
                {
                    Pending = statsEl.TryGetProperty("pending", out var pEl) ? pEl.GetInt32() : 0,
                    Processing = statsEl.TryGetProperty("processing", out var prEl) ? prEl.GetInt32() : 0,
                    Done = statsEl.TryGetProperty("done", out var dEl) ? dEl.GetInt32() : 0,
                    Failed = statsEl.TryGetProperty("failed", out var fEl) ? fEl.GetInt32() : 0,
                };
            }

            if (!envelope.Ok && string.IsNullOrWhiteSpace(envelope.ErrorMessage))
            {
                envelope.ErrorMessage = "Resposta da API sem detalhes de erro.";
            }

            return envelope;
        }
        catch (Exception error)
        {
            _logger.Error($"Falha ao interpretar resposta da API: {error.Message}");
            return new ApiEnvelope
            {
                Ok = false,
                ErrorMessage = "Resposta invalida do servidor.",
            };
        }
    }

    private string BuildUrl(string path)
    {
        var baseUrl = (_config.ServerUrl ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root))
        {
            throw new InvalidOperationException("PRINT_AGENT_SERVER_URL invalida.");
        }
        return new Uri(root, path.TrimStart('/')).ToString();
    }

    private sealed class ApiEnvelope
    {
        public bool Ok { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public PrintJob? Job { get; set; }
        public PrintJobsStats? Stats { get; set; }
    }
}
