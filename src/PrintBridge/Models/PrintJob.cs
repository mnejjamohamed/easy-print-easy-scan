namespace PrintBridge.Models;

/// <summary>
/// In-flight or completed print job.
/// </summary>
public sealed class PrintJob
{
    public int JobId { get; set; }
    public string PrinterName { get; set; } = string.Empty;
    public string DocumentName { get; set; } = "Untitled Document";
    public string MimeType { get; set; } = "application/pdf";
    public byte[] DocumentData { get; set; } = Array.Empty<byte>();
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public PrintJobState State { get; set; } = PrintJobState.Pending;
    public string? ErrorMessage { get; set; }

    public int FileSizeBytes => DocumentData?.Length ?? 0;

    public string FriendlySize =>
        FileSizeBytes > 1024 * 1024
            ? $"{(FileSizeBytes / 1048576.0):0.1} MB"
            : $"{(FileSizeBytes / 1024.0):0.0} KB";

    public string FriendlyState => State switch
    {
        PrintJobState.Completed => "Completed",
        PrintJobState.Processing => "Printing...",
        PrintJobState.Pending => "Pending",
        PrintJobState.Aborted => "Failed",
        PrintJobState.Canceled => "Canceled",
        _ => State.ToString()
    };
}

public enum PrintJobState
{
    Pending = 3,
    Processing = 5,
    Completed = 9,
    Aborted = 7,
    Canceled = 8
}
