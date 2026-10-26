namespace CardapioPrintAgent;

internal sealed class PrintJob
{
    public long Id { get; set; }
    public string OrderCode { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public string ClaimedBy { get; set; } = string.Empty;
}

internal sealed class PrintJobsStats
{
    public int Pending { get; set; }
    public int Processing { get; set; }
    public int Done { get; set; }
    public int Failed { get; set; }
}
