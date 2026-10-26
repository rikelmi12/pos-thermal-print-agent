namespace CardapioPrintAgent;

internal static class AppVersion
{
    public const string Display = "1.1.1";
    public const string Build = "job-id-dedup";

    public static string Label => $"v{Display} ({Build})";
}
