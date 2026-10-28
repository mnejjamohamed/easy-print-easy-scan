namespace PrintBridge.Models;

/// <summary>
/// Root settings saved to settings.json.
/// </summary>
public sealed class AppSettings
{
    public int IppPort { get; set; } = 631;
    public bool AutoStart { get; set; } = true;
    public List<PrinterConfig> Printers { get; set; } = new();
}
