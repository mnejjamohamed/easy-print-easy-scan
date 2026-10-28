namespace PrintBridge.Models;

/// <summary>
/// Configuration for a single shared Windows printer.
/// </summary>
public sealed class PrinterConfig
{
    public required string WindowsPrinterName { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool Color { get; set; } = true;
    public bool Duplex { get; set; } = false;
    public string Location { get; set; } = string.Empty;
}
