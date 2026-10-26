using System.Text;

namespace CardapioPrintAgent;

internal sealed class AgentLogger
{
    private readonly object _lock = new();

    public string Tail(int maxLines = 80)
    {
        AppPaths.EnsureDirectories();
        if (!File.Exists(AppPaths.LogPath)) return "Sem logs ainda.";

        try
        {
            using var stream = new FileStream(AppPaths.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader
                .ReadToEnd()
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length <= maxLines) return string.Join(Environment.NewLine, lines);
            return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - maxLines)));
        }
        catch (Exception error)
        {
            return $"Falha ao ler log: {error.Message}";
        }
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERRO", message);

    public void Clear()
    {
        AppPaths.EnsureDirectories();
        lock (_lock)
        {
            using var stream = new FileStream(AppPaths.LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            stream.SetLength(0);
        }
    }

    private void Write(string level, string message)
    {
        AppPaths.EnsureDirectories();
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";

        lock (_lock)
        {
            try
            {
                using var stream = new FileStream(AppPaths.LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(line);
            }
            catch
            {
                // Log nao pode derrubar o agente de impressao.
            }
        }
    }
}
