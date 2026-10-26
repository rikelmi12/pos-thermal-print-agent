namespace CardapioPrintAgent;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        AppPaths.EnsureDirectories();

        if (args.Any((arg) => string.Equals(arg, "--run-agent", StringComparison.OrdinalIgnoreCase)))
        {
            AgentRunner.RunHeadlessAsync(args).GetAwaiter().GetResult();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
