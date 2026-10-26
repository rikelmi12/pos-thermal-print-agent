using System.Text.Json;

namespace CardapioPrintAgent;

internal sealed class AgentStateStore
{
    private const int MaxTrackedJobs = 500;

    private readonly AgentLogger _logger;
    private readonly HashSet<string> _printedJobIds = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public AgentStateStore(AgentLogger logger)
    {
        _logger = logger;
        Load();
    }

    public bool WasPrinted(PrintJob job)
    {
        var jobId = job.Id > 0 ? job.Id.ToString() : string.Empty;
        return !string.IsNullOrWhiteSpace(jobId) && _printedJobIds.Contains(jobId);
    }

    public async Task MarkPrintedAsync(PrintJob job, CancellationToken cancellationToken = default)
    {
        if (job.Id > 0) _printedJobIds.Add(job.Id.ToString());
        await SaveAsync(cancellationToken);
    }

    public async Task ClearPrintedAsync(PrintJob job, CancellationToken cancellationToken = default)
    {
        if (job.Id > 0) _printedJobIds.Remove(job.Id.ToString());
        await SaveAsync(cancellationToken);
    }

    private void Load()
    {
        AppPaths.EnsureDirectories();
        if (!File.Exists(AppPaths.StatePath)) return;

        try
        {
            var raw = File.ReadAllText(AppPaths.StatePath);
            var payload = JsonSerializer.Deserialize<StatePayload>(raw, JsonOptions) ?? new StatePayload();
            foreach (var jobId in payload.PrintedJobIds.Where((x) => !string.IsNullOrWhiteSpace(x)).TakeLast(MaxTrackedJobs))
            {
                _printedJobIds.Add(jobId.Trim());
            }
        }
        catch (Exception error)
        {
            _logger.Error($"Falha ao carregar estado local do agente: {error.Message}");
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var payload = new StatePayload
            {
                PrintedJobIds = _printedJobIds.TakeLast(MaxTrackedJobs).ToList(),
                PrintedOrderCodes = [],
            };
            var serialized = JsonSerializer.Serialize(payload, JsonOptions) + "\n";
            await AppPaths.WriteTextAtomicAsync(AppPaths.StatePath, serialized, cancellationToken);
        }
        catch (Exception error)
        {
            _logger.Error($"Falha ao salvar estado local do agente: {error.Message}");
        }
    }

    private sealed class StatePayload
    {
        public List<string> PrintedJobIds { get; set; } = [];
        public List<string> PrintedOrderCodes { get; set; } = [];
    }
}
