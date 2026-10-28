namespace PrintBridge.Scanning;

public sealed class ScanOptions
{
    public int Dpi { get; set; } = 300;
    public bool Color { get; set; } = true;
    public bool UseAdf { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string Format { get; set; } = "jpeg";
}

public readonly record struct ScanResult(bool Success, IReadOnlyList<byte[]> Pages, string ContentType, string? Error)
{
    public byte[]? Data => Pages is { Count: > 0 } ? Pages[0] : null;
    public int PageCount => Pages?.Count ?? 0;

    public static ScanResult Ok(IReadOnlyList<byte[]> pages, string contentType) => new(true, pages, contentType, null);
    public static ScanResult Fail(string error) => new(false, Array.Empty<byte[]>(), "application/octet-stream", error);
}

public static class WiaScannerHelper
{
    private const string WiaFormatJpeg = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";
    private const string WiaFormatPng = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";
    private const int WiaDpsDocumentHandlingSelect = 3088;
    private const int WiaDpsPages = 3096;
    private const int WiaFeeder = 1;
    private const int WiaFlatbed = 2;
    private const int MaxScanPages = 100;
    private const uint WiaErrorPaperEmpty = 0x80210003;

    public static ScanResult ScanDocument(ScanOptions? options = null)
    {
        options ??= new ScanOptions();
        options.Dpi = Math.Clamp(options.Dpi, 75, 600);

        var result = ScanResult.Fail("The scan did not complete.");

        var thread = new Thread(() =>
        {
            try
            {
                result = ScanCore(options);
            }
            catch (Exception ex)
            {
                result = ScanResult.Fail(TranslateError(ex));
            }
        })
        {
            IsBackground = true,
            Name = "PrintBridge WIA Scan"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var timeout = TimeSpan.FromSeconds(options.UseAdf ? 600 : 150);
        if (!thread.Join(timeout))
            return ScanResult.Fail("The scanner did not respond in time. Check that it is powered on and connected.");

        return result;
    }

    public static IReadOnlyList<string> GetScannerNames()
    {
        var names = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                var managerType = Type.GetTypeFromProgID("WIA.DeviceManager");
                if (managerType == null) return;

                dynamic manager = Activator.CreateInstance(managerType)!;
                dynamic infos = manager.DeviceInfos;

                for (int i = 1; i <= infos.Count; i++)
                {
                    dynamic info = infos[i];
                    if ((int)info.Type != 1) continue;

                    try
                    {
                        var name = (string?)info.Properties["Name"].Value;
                        if (string.IsNullOrWhiteSpace(name))
                            name = (string?)info.DeviceID;

                        if (!string.IsNullOrWhiteSpace(name))
                            names.Add(name);
                    }
                    catch
                    {
                        try
                        {
                            var fallback = (string?)info.DeviceID;
                            if (!string.IsNullOrWhiteSpace(fallback))
                                names.Add(fallback);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
        })
        {
            IsBackground = true,
            Name = "PrintBridge WIA Enumeration"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));

        return names;
    }

    public static bool HasScannerInstalled() => GetScannerNames().Count > 0;

    private static ScanResult ScanCore(ScanOptions options)
    {
        var managerType = Type.GetTypeFromProgID("WIA.DeviceManager");
        if (managerType == null)
            return ScanResult.Fail("Windows Image Acquisition (WIA) is not available on this machine.");

        dynamic manager = Activator.CreateInstance(managerType)!;
        dynamic infos = manager.DeviceInfos;

        dynamic? selected = null;
        dynamic? firstScanner = null;

        for (int i = 1; i <= infos.Count; i++)
        {
            dynamic info = infos[i];
            if ((int)info.Type != 1) continue;

            if (firstScanner == null)
                firstScanner = info;

            if (!string.IsNullOrWhiteSpace(options.DeviceName))
            {
                try
                {
                    var name = (string?)info.Properties["Name"].Value;
                    var deviceId = (string?)info.DeviceID;

                    if (string.Equals(name, options.DeviceName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(deviceId, options.DeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = info;
                        break;
                    }
                }
                catch
                {
                }
            }
        }

        if (selected == null)
            selected = firstScanner;

        if (selected == null)
            return ScanResult.Fail("No scanner was detected. Connect your printer/scanner by USB and install its scanner driver.");

        dynamic device = selected.Connect();
        dynamic item = device.Items[1];
        ConfigureScanner(device, item, options);

        var format = options.Format.Equals("png", StringComparison.OrdinalIgnoreCase)
            ? WiaFormatPng
            : WiaFormatJpeg;
        var contentType = format == WiaFormatPng ? "image/png" : "image/jpeg";

        var pages = new List<byte[]>();

        while (pages.Count < MaxScanPages)
        {
            if (options.UseAdf)
            {
                SetWiaProperty(device.Properties, WiaDpsDocumentHandlingSelect, WiaFeeder);
                SetWiaProperty(item.Properties, WiaDpsDocumentHandlingSelect, WiaFeeder);
                SetWiaProperty(device.Properties, WiaDpsPages, 1);
                SetWiaProperty(item.Properties, WiaDpsPages, 1);
            }

            dynamic? imageFile = null;
            Exception? transferError = null;
            int attempts = options.UseAdf && pages.Count > 0 ? 8 : 1;

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                try
                {
                    imageFile = item.Transfer(format);
                    transferError = null;
                    break;
                }
                catch (Exception ex)
                {
                    transferError = ex;

                    if (IsPaperEmpty(ex))
                        break;

                    if (options.UseAdf && pages.Count > 0 && attempt == 2)
                    {
                        try
                        {
                            device = selected.Connect();
                            item = device.Items[1];
                            ConfigureScanner(device, item, options);
                        }
                        catch
                        {
                        }
                    }

                    if (attempt < attempts - 1)
                        Thread.Sleep(700);
                }
            }

            if (transferError != null)
            {
                if (IsPaperEmpty(transferError) || pages.Count > 0)
                    break;

                return ScanResult.Fail(TranslateError(transferError));
            }

            if (imageFile == null)
            {
                if (pages.Count > 0)
                    break;

                return ScanResult.Fail("The scanner did not return an image. Place the document on the glass and try again.");
            }

            byte[] bytes = (byte[])imageFile.FileData.BinaryData;
            if (bytes.Length == 0)
            {
                if (pages.Count > 0)
                    break;

                return ScanResult.Fail("The scanner returned an empty image.");
            }

            pages.Add(bytes);

            if (!options.UseAdf)
                break;
        }

        if (pages.Count == 0)
            return ScanResult.Fail("No pages were scanned. Load paper in the document feeder or place a document on the glass and try again.");

        return ScanResult.Ok(pages, contentType);
    }

    private static void ConfigureScanner(dynamic device, dynamic item, ScanOptions options)
    {
        SetWiaProperty(item.Properties, 6146, options.Color ? 1 : 2);
        SetWiaProperty(item.Properties, 6147, options.Dpi);
        SetWiaProperty(item.Properties, 6148, options.Dpi);

        int source = options.UseAdf ? WiaFeeder : WiaFlatbed;
        SetWiaProperty(device.Properties, WiaDpsDocumentHandlingSelect, source);
        SetWiaProperty(item.Properties, WiaDpsDocumentHandlingSelect, source);

        if (options.UseAdf)
        {
            SetWiaProperty(device.Properties, WiaDpsPages, 1);
            SetWiaProperty(item.Properties, WiaDpsPages, 1);
        }
    }

    private static void SetWiaProperty(dynamic properties, int propertyId, object value)
    {
        try
        {
            dynamic property = properties[propertyId];
            property.Value = value;
        }
        catch
        {
        }
    }

    private static bool IsPaperEmpty(Exception ex)
    {
        if (unchecked((uint)ex.HResult) == WiaErrorPaperEmpty)
            return true;

        return ex.Message.Contains("0x80210003", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("no paper", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("out of paper", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("feeder is empty", StringComparison.OrdinalIgnoreCase);
    }

    private static string TranslateError(Exception ex)
    {
        var message = ex.Message;
        uint code = unchecked((uint)ex.HResult);

        if (code == 0x80210003)
            return "No paper was detected in the document feeder. Load your pages and try again.";

        if (code == 0x80210002)
            return "Paper is jammed in the scanner. Remove the jammed page and try again.";

        if (code == 0x80210016)
            return "The scanner cover is open. Close it and try again.";

        if (code is 0x80210015 or 0x8021000F or 0x80210005
            || message.Contains("offline", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not connected", StringComparison.OrdinalIgnoreCase))
        {
            return "Scanner is offline or disconnected. Check the USB cable and printer power.";
        }

        if (code == 0x80210006 || message.Contains("busy", StringComparison.OrdinalIgnoreCase))
            return "Scanner is busy. Wait for the current job to finish and try again.";

        if (message.Contains("0x80210003", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no paper", StringComparison.OrdinalIgnoreCase)
            || message.Contains("out of paper", StringComparison.OrdinalIgnoreCase))
        {
            return "No paper was detected in the document feeder. Load your pages and try again.";
        }

        if (message.Contains("warming", StringComparison.OrdinalIgnoreCase))
            return "Scanner lamp is warming up. Try again in a few seconds.";

        if (code == 0x80210001 || message.Contains("paper", StringComparison.OrdinalIgnoreCase))
            return "The scanner could not feed the document. Check that paper is loaded correctly.";

        return message;
    }
}
