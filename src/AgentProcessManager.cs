using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace CardapioPrintAgent;

internal static class AgentProcessManager
{
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupRegistryValueName = "CardapioPrintAgent";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static bool IsAgentRunning()
    {
        return TryGetRunningAgentProcess() is not null;
    }

    public static Process? TryGetRunningAgentProcess()
    {
        if (!File.Exists(AppPaths.PidPath)) return null;

        try
        {
            var raw = File.ReadAllText(AppPaths.PidPath);
            var record = JsonSerializer.Deserialize<PidRecord>(raw, JsonOptions);
            if (record is null || record.Pid <= 0)
            {
                ClearPidFile();
                return null;
            }

            var process = Process.GetProcessById(record.Pid);
            if (process.HasExited)
            {
                ClearPidFile();
                return null;
            }

            var processPath = TryGetProcessPath(process);
            if (!string.IsNullOrWhiteSpace(processPath)
                && !Path.GetFullPath(processPath).Equals(Path.GetFullPath(AppPaths.InstalledExePath), StringComparison.OrdinalIgnoreCase)
                && !Path.GetFullPath(processPath).Equals(Path.GetFullPath(Environment.ProcessPath ?? string.Empty), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return process;
        }
        catch
        {
            ClearPidFile();
            return null;
        }
    }

    public static async Task StartAgentProcessAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        if (TryGetRunningAgentProcess() is not null) return;

        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = "--run-agent",
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppPaths.BaseDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Falha ao iniciar processo do agente.");

        await Task.Delay(400, cancellationToken);
        WritePidFile(process.Id);
    }

    public static void StopAgentProcess()
    {
        var process = TryGetRunningAgentProcess();
        if (process is not null)
        {
            try
            {
                process.Kill(true);
                process.WaitForExit(5000);
            }
            catch
            {
                // Ignora erros de encerramento forcado.
            }
        }

        ClearPidFile();
    }

    public static string EnsureInstalledBundle()
    {
        AppPaths.EnsureDirectories();
        RemoveLegacyStartupShortcut();

        var sourceExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(sourceExe))
        {
            throw new InvalidOperationException("Nao foi possivel detectar o caminho do executavel atual.");
        }

        var sourceExeFull = Path.GetFullPath(sourceExe);
        var installedExeFull = Path.GetFullPath(AppPaths.InstalledExePath);
        if (sourceExeFull.Equals(installedExeFull, StringComparison.OrdinalIgnoreCase))
        {
            RemoveLegacyInstalledExecutables(installedExeFull);
            return installedExeFull;
        }

        var sourceDir = Path.GetDirectoryName(sourceExeFull)
            ?? throw new InvalidOperationException("Diretorio do executavel atual invalido.");
        var sourceBaseName = Path.GetFileNameWithoutExtension(sourceExeFull);

        foreach (var sourceFile in Directory.GetFiles(sourceDir, $"{sourceBaseName}*"))
        {
            var targetFile = Path.Combine(AppPaths.BaseDir, Path.GetFileName(sourceFile));
            if (Path.GetFullPath(sourceFile).Equals(Path.GetFullPath(targetFile), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(sourceFile, targetFile, true);
        }

        File.Copy(sourceExeFull, installedExeFull, true);
        RemoveLegacyInstalledExecutables(installedExeFull);

        return installedExeFull;
    }

    public static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, false);
        var raw = key?.GetValue(StartupRegistryValueName) as string;
        return !string.IsNullOrWhiteSpace(raw);
    }

    public static void ConfigureStartup(bool enabled, string executablePath)
    {
        RemoveLegacyStartupShortcut();

        using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, true)
            ?? throw new InvalidOperationException("Falha ao abrir registro do Windows para inicializacao automatica.");

        if (!enabled)
        {
            key.DeleteValue(StartupRegistryValueName, false);
            return;
        }

        var command = $"\"{executablePath}\" --run-agent";
        key.SetValue(StartupRegistryValueName, command, RegistryValueKind.String);
    }

    public static void WritePidFile(int pid)
    {
        var record = new PidRecord
        {
            Pid = pid,
            WrittenAtUtc = DateTime.UtcNow,
        };
        var payload = JsonSerializer.Serialize(record, JsonOptions) + "\n";
        File.WriteAllText(AppPaths.PidPath, payload);
    }

    public static void ClearPidFile()
    {
        try
        {
            if (File.Exists(AppPaths.PidPath)) File.Delete(AppPaths.PidPath);
        }
        catch
        {
            // Ignora erros de limpeza.
        }
    }

    private static string TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void RemoveLegacyInstalledExecutables(string installedExeFull)
    {
        try
        {
            var installedDirectory = Path.GetFullPath(AppPaths.BaseDir);
            if (!Directory.Exists(installedDirectory)) return;

            foreach (var exePath in Directory.GetFiles(installedDirectory, "*.exe"))
            {
                var exeFullPath = Path.GetFullPath(exePath);
                if (exeFullPath.Equals(installedExeFull, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Delete(exeFullPath);
            }
        }
        catch
        {
            // Ignora lixo antigo que esteja bloqueado por Windows/antivirus.
        }
    }

    private static void RemoveLegacyStartupShortcut()
    {
        try
        {
            var startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (string.IsNullOrWhiteSpace(startupDirectory)) return;

            var legacyVbsPath = Path.Combine(startupDirectory, "CardapioPrintAgent.vbs");
            if (File.Exists(legacyVbsPath))
            {
                File.Delete(legacyVbsPath);
            }
        }
        catch
        {
            // O registro do Windows passa a ser a fonte correta do iniciar com Windows.
        }
    }

    private sealed class PidRecord
    {
        public int Pid { get; set; }
        public DateTime WrittenAtUtc { get; set; }
    }
}
