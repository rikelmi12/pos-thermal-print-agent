using System.Text;

namespace CardapioPrintAgent;

internal static class AppPaths
{
    private const string AppFolderName = "CardapioPrintAgent";

    public static string BaseDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName
    );

    public static string ConfigPath { get; } = Path.Combine(BaseDir, "agent-config.json");
    public static string StatePath { get; } = Path.Combine(BaseDir, "agent-state.json");
    public static string LogPath { get; } = Path.Combine(BaseDir, "agent.log");
    public static string PidPath { get; } = Path.Combine(BaseDir, "agent.pid.json");
    public static string InstalledExePath { get; } = Path.Combine(BaseDir, "CardapioPrintAgentInstaller.exe");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(BaseDir);
    }

    public static async Task WriteTextAtomicAsync(string filePath, string content, CancellationToken cancellationToken = default)
    {
        EnsureDirectories();
        var tmpPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tmpPath, content, new UTF8Encoding(false), cancellationToken);
        File.Move(tmpPath, filePath, true);
    }
}
