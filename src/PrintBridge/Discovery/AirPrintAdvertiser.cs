using Makaretu.Dns;
using PrintBridge.Models;
using PrintBridge.Network;
using System.Net;

namespace PrintBridge.Discovery;

/// <summary>
/// Broadcasts DNS-SD / mDNS announcements on the local Wi-Fi / LAN so that
/// Apple devices (AirPrint) and Android devices (Mopria / IPP Everywhere)
/// discover shared printers automatically.
/// </summary>
public sealed class AirPrintAdvertiser : IDisposable
{
    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;
    private readonly List<ServiceProfile> _profiles = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Starts advertising all enabled printers.
    /// </summary>
    public void Start(IEnumerable<PrinterConfig> printers, int ippPort)
    {
        lock (_lock)
        {
            Stop();

            try
            {
                _mdns = new MulticastService();
                _sd = new ServiceDiscovery(_mdns);

                var hostName = NetworkHelper.GetHostName();
                var localIpStr = NetworkHelper.GetLocalIPv4Address();
                var ipList = new List<IPAddress>();
                if (IPAddress.TryParse(localIpStr, out var localIp) && !IPAddress.IsLoopback(localIp))
                {
                    ipList.Add(localIp);
                }

                foreach (var printer in printers.Where(p => p.Enabled))
                {
                    var safeName = SanitizeServiceName(string.IsNullOrWhiteSpace(printer.DisplayName)
                        ? printer.WindowsPrinterName
                        : printer.DisplayName);

                    // Service type _ipp._tcp is used by AirPrint, Mopria, and IPP Everywhere
                    var profile = new ServiceProfile(safeName, "_ipp._tcp", (ushort)ippPort, ipList.Count > 0 ? ipList : null);

                    try
                    {
                        profile.HostName = DomainName.Join(hostName, "local");
                    }
                    catch { }

                    var safeQueue = PrinterIdentity.GetQueuePath(printer);

                    profile.AddProperty("txtvers", "1");
                    profile.AddProperty("qtotal", "1");
                    profile.AddProperty("rp", $"printers/{safeQueue}");
                    profile.AddProperty("ty", printer.DisplayName);
                    profile.AddProperty("note", string.IsNullOrEmpty(printer.Location) ? "Windows Wi-Fi Printer" : printer.Location);
                    profile.AddProperty("product", $"({safeName})");
                    profile.AddProperty("adminurl", $"http://{localIpStr}:{ippPort}/");
                    profile.AddProperty("air", "none");
                    profile.AddProperty("kind", "document,photo,envelope,label");
                    profile.AddProperty("pdl", "application/pdf,image/jpeg,image/png,image/urf");

                    var urfList = new List<string> { "W8", "CP1", "RS300-600" };
                    if (printer.Color) urfList.Insert(1, "SRGB24");
                    if (printer.Duplex) urfList.Add("DM1");
                    profile.AddProperty("URF", string.Join(",", urfList));

                    profile.AddProperty("Color", printer.Color ? "T" : "F");
                    profile.AddProperty("Duplex", printer.Duplex ? "T" : "F");
                    profile.AddProperty("Scan", "F");
                    profile.AddProperty("Fax", "F");
                    profile.AddProperty("usb_MFG", "PrintBridge");
                    profile.AddProperty("usb_MDL", safeName);
                    profile.AddProperty("priority", "10");
                    profile.AddProperty("UUID", PrinterIdentity.GetUuid(printer.WindowsPrinterName));

                    // Subtype _universal is required by Apple AirPrint
                    profile.Subtypes.Add("_universal");

                    _profiles.Add(profile);
                    _sd.Advertise(profile);
                }

                _mdns.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"mDNS error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Stops advertising and unregisters services.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_sd != null)
            {
                foreach (var p in _profiles)
                {
                    try { _sd.Unadvertise(p); } catch { }
                }
            }
            _profiles.Clear();

            try { _sd?.Dispose(); } catch { }
            _sd = null;

            try { _mdns?.Stop(); } catch { }
            try { _mdns?.Dispose(); } catch { }
            _mdns = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private static string SanitizeServiceName(string name)
    {
        var clean = name.Replace('.', ' ').Replace('\\', ' ').Replace('/', ' ').Trim();
        if (clean.Length > 60) clean = clean[..60];
        return string.IsNullOrWhiteSpace(clean) ? "Windows Printer" : clean;
    }
}
