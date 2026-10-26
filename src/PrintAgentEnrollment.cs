using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CardapioPrintAgent;

internal sealed class PrintAgentEnrollment
{
    private const string EnrollmentUrl = "https://softcore.cloud/api/print-agent/enroll";

    private static readonly Regex SetupFilenamePattern = new(
        @"^(?:cardapio-print-agent|CardapioPrintAgent)-setup-(?<code>[A-Za-z0-9_-]{43})(?:\s*\(\d+\))?\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string TryGetCodeFromExecutablePath(string? executablePath)
    {
        var filename = Path.GetFileName((executablePath ?? string.Empty).Trim());
        if (filename.Length == 0) return string.Empty;
        var match = SetupFilenamePattern.Match(filename);
        return match.Success ? match.Groups["code"].Value : string.Empty;
    }

    public static async Task<EnrollmentResponse> EnrollAsync(
        string code,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        if (!SetupCodeIsValid(code))
        {
            throw new InvalidOperationException("Codigo de configuracao invalido. Baixe o agente novamente pelo painel.");
        }

        var safeClientId = (clientId ?? string.Empty).Trim();
        if (safeClientId.Length == 0 || safeClientId.Length > 120)
        {
            throw new InvalidOperationException("Identificacao deste computador invalida.");
        }

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        var body = JsonSerializer.Serialize(new { code, clientId = safeClientId });
        using var request = new HttpRequestMessage(HttpMethod.Post, EnrollmentUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Print-Agent-Id", safeClientId);
        request.Headers.Accept.ParseAdd("application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("O servidor demorou para configurar o agente. Tente baixar novamente pelo painel.");
        }
        catch (HttpRequestException error)
        {
            throw new InvalidOperationException($"Nao foi possivel conectar ao servidor de configuracao: {error.Message}", error);
        }

        using (response)
        {
            if (IsRedirect(response.StatusCode))
            {
                throw new InvalidOperationException("O servidor de configuracao respondeu com redirecionamento inesperado.");
            }

            var raw = await response.Content.ReadAsStringAsync(timeout.Token);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!IsJsonMediaType(mediaType))
            {
                throw new InvalidOperationException(
                    $"O servidor de configuracao respondeu em formato inesperado (HTTP {(int)response.StatusCode}).");
            }

            EnrollmentResponse? payload;
            try
            {
                payload = JsonSerializer.Deserialize<EnrollmentResponse>(raw, JsonOptions);
            }
            catch (JsonException error)
            {
                throw new InvalidOperationException("O servidor retornou uma resposta de configuracao invalida.", error);
            }

            if (!response.IsSuccessStatusCode || payload?.Ok != true)
            {
                var message = (payload?.Error ?? string.Empty).Trim();
                if (message.Length == 0)
                {
                    message = response.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound
                        ? "Este instalador expirou ou ja foi utilizado. Baixe outro pelo painel."
                        : $"Nao foi possivel configurar o agente (HTTP {(int)response.StatusCode}).";
                }
                throw new InvalidOperationException(message);
            }

            payload.Validate();
            return payload;
        }
    }

    private static bool SetupCodeIsValid(string? code)
    {
        var value = code ?? string.Empty;
        return value.Length == 43 && value.All((character) =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    private static bool IsJsonMediaType(string? mediaType)
    {
        var value = (mediaType ?? string.Empty).Trim();
        return value.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        var value = (int)statusCode;
        return value is >= 300 and <= 399;
    }
}

internal sealed class EnrollmentResponse
{
    public bool Ok { get; set; }
    public string Error { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public EnrollmentDefaults Defaults { get; set; } = new();

    public void Validate()
    {
        Token = (Token ?? string.Empty).Trim();
        ClientId = (ClientId ?? string.Empty).Trim();
        ServerUrl = AgentConfig.NormalizeServerUrl(ServerUrl);

        if (Token.Length == 0)
        {
            throw new InvalidOperationException("O servidor nao entregou a credencial do agente.");
        }
        if (ClientId.Length == 0 || ClientId.Length > 120)
        {
            throw new InvalidOperationException("O servidor entregou uma identificacao de agente invalida.");
        }
        if (!ServerUrl.Equals(AgentConfig.CanonicalServerUrl, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("O servidor entregou um endereco de agente inesperado.");
        }

        Defaults ??= new EnrollmentDefaults();
    }

    public void ApplyTo(AgentConfig config, bool preserveDeviceSettings)
    {
        config.ServerUrl = AgentConfig.CanonicalServerUrl;
        config.Token = Token;
        config.ClientId = ClientId;

        if (!preserveDeviceSettings)
        {
            var defaults = Defaults ?? new EnrollmentDefaults();
            if (!string.IsNullOrWhiteSpace(defaults.PrintMode)) config.PrintMode = defaults.PrintMode;
            if (defaults.PaperWidthMm.HasValue) config.PaperWidthMm = defaults.PaperWidthMm.Value;
            if (defaults.FeedAfterPrintMm.HasValue) config.FeedAfterPrintMm = defaults.FeedAfterPrintMm.Value;
            if (defaults.PollMs.HasValue) config.PollMs = defaults.PollMs.Value;
            if (defaults.StartWithWindows.HasValue) config.StartWithWindows = defaults.StartWithWindows.Value;
        }

        config.Normalize();
    }
}

internal sealed class EnrollmentDefaults
{
    public string PrintMode { get; set; } = string.Empty;
    public int? PaperWidthMm { get; set; }
    public int? FeedAfterPrintMm { get; set; }
    public int? PollMs { get; set; }
    public bool? StartWithWindows { get; set; }
}
