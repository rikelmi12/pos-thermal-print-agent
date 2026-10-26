using CardapioPrintAgent;
using System.Text;
using System.Text.Json;

var failures = new List<string>();

void Check(bool condition, string name)
{
    if (condition)
    {
        Console.WriteLine($"OK  {name}");
        return;
    }

    failures.Add(name);
    Console.Error.WriteLine($"ERRO {name}");
}

var setupCode = new string('A', 43);
Check(
    PrintAgentEnrollment.TryGetCodeFromExecutablePath($@"C:\Downloads\cardapio-print-agent-setup-{setupCode}.exe") == setupCode,
    "extrai codigo do filename oficial");
Check(
    PrintAgentEnrollment.TryGetCodeFromExecutablePath($@"C:\Downloads\cardapio-print-agent-setup-{setupCode} (1).exe") == setupCode,
    "aceita sufixo de download do navegador");
Check(
    PrintAgentEnrollment.TryGetCodeFromExecutablePath($@"C:\Downloads\CardapioPrintAgent-setup-{setupCode}.exe") == setupCode,
    "aceita nome legado do instalador");
Check(
    PrintAgentEnrollment.TryGetCodeFromExecutablePath(@"C:\Downloads\cardapio-print-agent-setup-curto.exe") == string.Empty,
    "rejeita codigo com tamanho invalido");

Check(
    AgentConfig.NormalizeServerUrl("https://www.softcore.cloud/") == AgentConfig.CanonicalServerUrl,
    "migra www para origem canonica");
Check(
    AgentConfig.NormalizeServerUrl("http://softcore.cloud") == AgentConfig.CanonicalServerUrl,
    "forca HTTPS no host oficial");
Check(
    AgentConfig.NormalizeServerUrl("http://localhost:8000/") == "http://localhost:8000",
    "preserva desenvolvimento local");

var firstFreshClientId = new AgentConfig().ClientId;
var secondFreshClientId = new AgentConfig().ClientId;
Check(firstFreshClientId != secondFreshClientId, "gera identificacao unica por instalacao nova");
Check(firstFreshClientId.EndsWith("-print-agent", StringComparison.Ordinal), "mantem sufixo legivel do Client ID");

var existing = new AgentConfig
{
    ServerUrl = "https://www.softcore.cloud",
    Token = "credencial-antiga",
    PrinterName = "ELGIN i7(USB)",
    ClientId = "EXCLUSIVA-print-agent",
    PrintMode = "thermal",
    PaperWidthMm = 80,
    FeedAfterPrintMm = 15,
    PollMs = 1000,
    StartWithWindows = true,
};
var enrolled = new EnrollmentResponse
{
    Ok = true,
    ServerUrl = AgentConfig.CanonicalServerUrl,
    Token = "credencial-individual-nova",
    ClientId = "EXCLUSIVA-print-agent",
    Defaults = new EnrollmentDefaults
    {
        PrintMode = "a4",
        PaperWidthMm = 210,
        FeedAfterPrintMm = 3,
        PollMs = 9000,
        StartWithWindows = false,
    },
};
enrolled.Validate();
enrolled.ApplyTo(existing, preserveDeviceSettings: true);
Check(existing.ServerUrl == AgentConfig.CanonicalServerUrl, "aplica servidor canonico");
Check(existing.Token == "credencial-individual-nova", "aplica credencial individual");
Check(existing.PrinterName == "ELGIN i7(USB)", "preserva impressora existente");
Check(existing.PrintMode == "thermal" && existing.PaperWidthMm == 80, "preserva papel existente");
Check(existing.FeedAfterPrintMm == 15 && existing.PollMs == 1000, "preserva temporizacoes existentes");
Check(existing.StartWithWindows, "preserva inicializacao existente");

var fresh = new AgentConfig();
enrolled.ApplyTo(fresh, preserveDeviceSettings: false);
Check(fresh.PrintMode == "a4" && fresh.PaperWidthMm == 210, "aplica papel padrao em instalacao nova");
Check(fresh.FeedAfterPrintMm == 3 && fresh.PollMs == 9000, "aplica defaults em instalacao nova");
Check(!fresh.StartWithWindows, "aplica default de inicializacao em instalacao nova");

var secret = "token-de-teste-que-nao-pode-ficar-em-texto-puro";
var protectedToken = TokenProtector.Protect(secret);
Check(protectedToken.StartsWith("dpapi:v1:", StringComparison.Ordinal), "protege token com envelope DPAPI");
Check(!protectedToken.Contains(secret, StringComparison.Ordinal), "ciphertext nao contem token plaintext");
Check(TokenProtector.Unprotect(protectedToken) == secret, "DPAPI faz round-trip no mesmo usuario");

var serialized = JsonSerializer.Serialize(new AgentConfig
{
    Token = secret,
    ProtectedToken = protectedToken,
});
Check(!serialized.Contains(secret, StringComparison.Ordinal), "modelo JSON ignora token plaintext");
using var serializedDocument = JsonDocument.Parse(serialized);
var serializedProtectedToken = serializedDocument.RootElement.GetProperty("ProtectedToken").GetString();
Check(serializedProtectedToken == protectedToken, "modelo JSON persiste somente token protegido");

bool ContainsBytes(byte[] source, params byte[] expected)
{
    for (var index = 0; index <= source.Length - expected.Length; index += 1)
    {
        if (source.AsSpan(index, expected.Length).SequenceEqual(expected)) return true;
    }
    return false;
}

var styledReceipt = RawThermalPrinter.BuildEscPosPayload(
    "PEDIDO\n[[BF_OBS_START]]\nObs: SEM CEBOLA\n[[BF_OBS_END]]\nTOTAL",
    58,
    15);
var styledReceiptText = Encoding.ASCII.GetString(styledReceipt);
Check(ContainsBytes(styledReceipt, 0x1B, 0x45, 0x01), "ativa negrito ESC/POS na observacao");
Check(ContainsBytes(styledReceipt, 0x1D, 0x21, 0x01), "ativa altura dupla ESC/POS na observacao");
Check(ContainsBytes(styledReceipt, 0x1B, 0x45, 0x00), "restaura negrito apos observacao");
Check(!styledReceiptText.Contains("[[BF_OBS_", StringComparison.Ordinal), "remove marcadores antes da impressao");

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} teste(s) falharam: {string.Join(", ", failures)}");
    return 1;
}

Console.WriteLine("Todos os testes do agente passaram.");
return 0;
