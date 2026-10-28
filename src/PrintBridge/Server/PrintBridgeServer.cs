using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrintBridge.Discovery;
using PrintBridge.Ipp;
using PrintBridge.Models;
using PrintBridge.Printing;
using PrintBridge.Settings;

namespace PrintBridge.Server;

/// <summary>
/// Manages the embedded IPP Kestrel server and the mDNS Bonjour advertiser.
/// Runs in the background of the tray application.
/// </summary>
public sealed class PrintBridgeServer : IDisposable
{
    private readonly SettingsManager _settings;
    private readonly WindowsPrinterBridge _printerBridge;
    private readonly AirPrintAdvertiser _advertiser;
    private readonly IppRequestHandler _ippHandler;
    private WebApplication? _webApp;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _httpStarted;
    private int _boundPort;
    private readonly object _lock = new();

    public event Action<PrintJob>? JobReceived;
    public bool IsRunning => _isRunning;
    public int BoundPort => _boundPort > 0 ? _boundPort : _settings.Current.IppPort;
    public string? LastError { get; private set; }

    public IReadOnlyList<PrintJob> GetAllJobs() => _ippHandler.GetAllJobs();
    public void ClearJobs() => _ippHandler.ClearJobs();
    public PrintJob SubmitDirectJob(string printerName, string docName, string mimeType, byte[] data, PrintOptions? options = null) =>
        _ippHandler.SubmitDirectJob(printerName, docName, mimeType, data, options);

    public PrintBridgeServer(SettingsManager settings)
    {
        _settings = settings;
        _printerBridge = new WindowsPrinterBridge();
        _advertiser = new AirPrintAdvertiser();
        _ippHandler = new IppRequestHandler(_settings, _printerBridge);

        _ippHandler.JobReceived += job => JobReceived?.Invoke(job);
    }

    /// <summary>
    /// Starts the IPP HTTP listener and the mDNS service advertiser.
    /// </summary>
    public async Task StartAsync()
    {
        lock (_lock)
        {
            if (_isRunning) return;
            _cts = new CancellationTokenSource();
            _isRunning = true;
            _httpStarted = false;
            _boundPort = 0;
            LastError = null;
        }

        // 1. Auto-discover Windows printers if initial configuration is empty
        AutoDiscoverPrintersIfEmpty();

        var port = _settings.Current.IppPort;

        // 2. Start Kestrel Web Server
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders(); // Keep console quiet during UI operation

            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(port);
            });

            builder.Services.AddSingleton(_settings);
            builder.Services.AddSingleton(_printerBridge);
            builder.Services.AddSingleton(_ippHandler);

            _webApp = builder.Build();
            _webApp.UseMiddleware<IppMiddleware>();

            await _webApp.StartAsync(_cts.Token);
            _httpStarted = true;
            _boundPort = port;
        }
        catch (Exception ex)
        {
            LastError = $"Could not start the print server on port {port}: {ex.Message}";
            System.Diagnostics.Debug.WriteLine(LastError);

            if (_webApp != null)
            {
                try { await _webApp.DisposeAsync(); } catch { }
                _webApp = null;
            }
        }

        if (!_httpStarted)
        {
            lock (_lock)
                _isRunning = false;

            return;
        }

        // 3. Start mDNS DNS-SD Advertiser for AirPrint & Mopria
        try
        {
            _advertiser.Start(_settings.Current.Printers, port);
        }
        catch (Exception ex)
        {
            LastError = $"Failed to start AirPrint discovery: {ex.Message}";
            System.Diagnostics.Debug.WriteLine(LastError);
        }
    }

    /// <summary>
    /// Updates the mDNS advertisement immediately when settings or printer toggles change.
    /// </summary>
    public void RefreshAdvertisement()
    {
        if (!_httpStarted) return;

        try
        {
            _advertiser.Start(_settings.Current.Printers, BoundPort);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error updating mDNS: {ex.Message}");
        }
    }

    /// <summary>
    /// Automatically populates printers list from Windows Spooler on first run.
    /// </summary>
    public void AutoDiscoverPrintersIfEmpty()
    {
        if (_settings.Current.Printers.Count == 0)
        {
            var installed = WindowsPrinterBridge.GetInstalledPrinters();
            foreach (var name in installed)
            {
                _settings.Current.Printers.Add(new PrinterConfig
                {
                    WindowsPrinterName = name,
                    DisplayName = name,
                    Enabled = true,
                    Color = true,
                    Duplex = false
                });
            }
            _settings.Save();
        }
    }

    public async Task StopAsync()
    {
        lock (_lock)
        {
            if (!_isRunning) return;
            _isRunning = false;
            _httpStarted = false;
            _boundPort = 0;
        }

        _advertiser.Stop();

        if (_webApp != null)
        {
            try
            {
                _cts?.Cancel();
                await _webApp.StopAsync();
                await _webApp.DisposeAsync();
            }
            catch { }
            _webApp = null;
        }

        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        _ = StopAsync();
        _advertiser.Dispose();
    }
}
