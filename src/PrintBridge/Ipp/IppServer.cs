using PrintBridge.Models;
using PrintBridge.Network;
using PrintBridge.Printing;
using PrintBridge.Settings;
using System.Collections.Concurrent;

namespace PrintBridge.Ipp;

/// <summary>
/// Core IPP request handler responding to AirPrint and IPP Everywhere operations.
/// </summary>
public sealed class IppRequestHandler
{
    private readonly SettingsManager _settings;
    private readonly WindowsPrinterBridge _printerBridge;
    private readonly ConcurrentDictionary<int, PrintJob> _jobs = new();
    private static readonly DateTime _startTime = DateTime.UtcNow;
    private int _nextJobId = 1;

    public event Action<PrintJob>? JobReceived;

    public IReadOnlyList<PrintJob> GetAllJobs() =>
        _jobs.Values.OrderByDescending(j => j.ReceivedAt).ToList();

    public void ClearJobs() => _jobs.Clear();

    public PrintJob SubmitDirectJob(
        string printerName,
        string docName,
        string mimeType,
        byte[] data,
        PrintOptions? options = null)
    {
        var cfg = FindPrinter(printerName);
        var targetQueue = cfg?.WindowsPrinterName ?? printerName;

        var jobId = Interlocked.Increment(ref _nextJobId);
        var job = new PrintJob
        {
            JobId = jobId,
            PrinterName = targetQueue,
            DocumentName = docName,
            MimeType = mimeType,
            DocumentData = data,
            State = PrintJobState.Processing
        };
        _jobs[jobId] = job;
        JobReceived?.Invoke(job);

        var result = _printerBridge.SendToSpooler(targetQueue, data, docName, mimeType, options);
        job.State = result.Success ? PrintJobState.Completed : PrintJobState.Aborted;
        if (!result.Success) job.ErrorMessage = result.Error ?? "Spooler submission failed";
        return job;
    }

    public IppRequestHandler(SettingsManager settings, WindowsPrinterBridge printerBridge)
    {
        _settings = settings;
        _printerBridge = printerBridge;
    }

    public byte[] HandleRequest(byte[] requestData, string printerName, string? requestedPrinterUri = null)
    {
        try
        {
            var req = IppRequest.Parse(requestData);

            if (string.IsNullOrWhiteSpace(printerName))
            {
                var uri = req.GetOperationAttribute("printer-uri");
                if (!string.IsNullOrWhiteSpace(uri))
                    printerName = ExtractQueueFromUri(uri);
            }

            printerName = ExtractQueueFromUri(printerName);

            var cfg = FindPrinter(printerName);
            var resolvedName = cfg?.WindowsPrinterName ?? printerName;
            if (string.IsNullOrWhiteSpace(resolvedName))
            {
                resolvedName = _settings.Current.Printers.FirstOrDefault(p => p.Enabled)?.WindowsPrinterName ?? "Printer";
            }

            var printerUri = ResolvePrinterUri(resolvedName, requestedPrinterUri);

            return req.OperationId switch
            {
                IppEncoding.OpGetPrinterAttributes => HandleGetPrinterAttributes(req, resolvedName, printerUri),
                IppEncoding.OpPrintJob => HandlePrintJob(req, resolvedName, printerUri),
                IppEncoding.OpValidateJob => HandleValidateJob(req, resolvedName),
                IppEncoding.OpGetJobs => HandleGetJobs(req, resolvedName, printerUri),
                IppEncoding.OpGetJobAttributes => HandleGetJobAttributes(req, printerUri),
                IppEncoding.OpCancelJob => HandleCancelJob(req),
                IppEncoding.OpCreateJob => HandleCreateJob(req, resolvedName, printerUri),
                IppEncoding.OpSendDocument => HandleSendDocument(req, resolvedName, printerUri),
                _ => BuildErrorResponse(req, IppEncoding.StatusServerError, "Operation not supported")
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"IPP error: {ex}");
            return BuildMinimalErrorResponse();
        }
    }

    private byte[] HandleGetPrinterAttributes(IppRequest req, string printerName, string printerUri)
    {
        var cfg = FindPrinter(printerName);
        var ip = NetworkHelper.GetLocalIPv4Address();
        var port = _settings.Current.IppPort;
        var displayName = !string.IsNullOrWhiteSpace(cfg?.DisplayName) ? cfg.DisplayName : printerName;
        var queueName = cfg != null ? PrinterIdentity.GetQueuePath(cfg) : printerName.Replace(' ', '_');
        var uuid = PrinterIdentity.GetUuid(printerName);
        var queuedJobs = _jobs.Values.Count(j => j.State is PrintJobState.Pending or PrintJobState.Processing);
        var supportsColor = cfg?.Color ?? true;
        var supportsDuplex = cfg?.Duplex ?? false;
        var location = string.IsNullOrWhiteSpace(cfg?.Location) ? "Local Network" : cfg!.Location;

        var rb = new IppResponseBuilder(req.VersionMajor, req.VersionMinor, IppEncoding.StatusOk, req.RequestId);

        rb.BeginOperationAttributes()
          .AddCharset("attributes-charset", "utf-8")
          .AddNaturalLanguage("attributes-natural-language", "en");

        rb.BeginPrinterAttributes()
          .AddUri("printer-uri-supported", printerUri)
          .AddKeyword("uri-security-supported", "none")
          .AddKeyword("uri-authentication-supported", "none")
          .AddName("printer-name", queueName)
          .AddEnum("printer-state", 3) // 3 = idle
          .AddKeyword("printer-state-reasons", "none")
          .AddKeyword("ipp-versions-supported", "1.1")
            .AddKeywordValue("2.0")
          .AddEnum("operations-supported", IppEncoding.OpPrintJob)
            .AddEnumValue(IppEncoding.OpValidateJob)
            .AddEnumValue(IppEncoding.OpGetJobs)
            .AddEnumValue(IppEncoding.OpGetPrinterAttributes)
            .AddEnumValue(IppEncoding.OpCancelJob)
            .AddEnumValue(IppEncoding.OpGetJobAttributes)
            .AddEnumValue(IppEncoding.OpCreateJob)
            .AddEnumValue(IppEncoding.OpSendDocument)
          .AddCharset("charset-configured", "utf-8")
          .AddCharset("charset-supported", "utf-8")
          .AddNaturalLanguage("natural-language-configured", "en")
          .AddNaturalLanguage("generated-natural-language-supported", "en")
          .AddMimeMediaType("document-format-default", "application/pdf")
          .AddMimeMediaType("document-format-preferred", "application/pdf")
          .AddMimeMediaType("document-format-supported", "application/pdf")
            .AddMimeMediaTypeValue("image/jpeg")
            .AddMimeMediaTypeValue("image/png")
            .AddMimeMediaTypeValue("image/urf")
          .AddUri("printer-uuid", $"urn:uuid:{uuid}");

        rb.AddKeyword("urf-supported", "W8");
        if (supportsColor)
            rb.AddKeywordValue("SRGB24");
        rb.AddKeywordValue("CP1").AddKeywordValue("RS300-600");
        if (supportsDuplex)
            rb.AddKeywordValue("DM1");

        rb.AddKeyword("compression-supported", "none")
          .AddText("printer-info", displayName)
          .AddText("printer-location", location)
          .AddText("printer-make-and-model", $"PrintBridge ({displayName})")
          .AddEnum("printer-type", 0)
          .AddBoolean("color-supported", supportsColor)
          .AddKeyword("print-color-mode-supported", supportsColor ? "color" : "monochrome");

        if (supportsColor)
            rb.AddKeywordValue("monochrome");

        rb.AddKeyword("print-color-mode-default", supportsColor ? "color" : "monochrome")
          .AddKeyword("sides-supported", "one-sided");

        if (supportsDuplex)
            rb.AddKeywordValue("two-sided-long-edge").AddKeywordValue("two-sided-short-edge");

        rb.AddKeyword("sides-default", supportsDuplex ? "two-sided-long-edge" : "one-sided")
          .AddEnum("print-quality-supported", 3)
            .AddEnumValue(4)
            .AddEnumValue(5)
          .AddEnum("print-quality-default", 4)
          .AddInteger("copies-default", 1)
          .AddRangeOfInteger("copies-supported", 1, 99)
          .AddKeyword("media-default", "iso_a4_210x297mm")
          .AddKeyword("media-supported", "iso_a4_210x297mm")
            .AddKeywordValue("na_letter_8.5x11in")
            .AddKeywordValue("na_legal_8.5x14in")
          .AddKeyword("output-bin-default", "face-down")
          .AddKeyword("output-bin-supported", "face-down")
          .AddText("printer-device-id", $"MFG:PrintBridge;MDL:{displayName};")
          .AddBoolean("printer-is-accepting-jobs", true)
          .AddInteger("queued-job-count", queuedJobs)
          .AddKeyword("pdl-override-supported", "attempted")
          .AddInteger("printer-up-time", (int)(DateTime.UtcNow - _startTime).TotalSeconds)
          .AddKeyword("which-jobs-supported", "completed")
            .AddKeywordValue("not-completed")
          .AddUri("printer-more-info", $"http://{ip}:{port}/")
          .AddName("printer-dns-sd-name", displayName)
          .AddText("marker-names", "Black Ink")
            .AddTextValue("Color Ink")
          .AddKeyword("marker-types", "inkCartridge")
            .AddKeywordValue("inkCartridge")
          .AddKeyword("marker-colors", "#000000")
            .AddKeywordValue("#00FFFF#FF00FF#FFFF00")
          .AddInteger("marker-levels", 100)
            .AddIntegerValue(100)
          .AddInteger("marker-high-levels", 100)
            .AddIntegerValue(100)
          .AddInteger("marker-low-levels", 10)
            .AddIntegerValue(10);

        return rb.Build();
    }

    private byte[] HandlePrintJob(IppRequest req, string printerName, string printerUri)
    {
        var docName = req.GetOperationAttribute("job-name") ?? "Mobile Print Document";
        var mimeType = req.GetOperationAttribute("document-format") ?? "application/pdf";
        var options = ParsePrintOptions(req);

        if (req.DocumentData == null || req.DocumentData.Length == 0)
            return BuildErrorResponse(req, IppEncoding.StatusClientBadRequest, "No document data provided.");

        var jobId = Interlocked.Increment(ref _nextJobId);
        var job = new PrintJob
        {
            JobId = jobId,
            PrinterName = printerName,
            DocumentName = docName,
            MimeType = mimeType,
            DocumentData = req.DocumentData,
            State = PrintJobState.Processing
        };

        _jobs[jobId] = job;
        JobReceived?.Invoke(job);

        var result = _printerBridge.SendToSpooler(printerName, req.DocumentData, docName, mimeType, options);
        job.State = result.Success ? PrintJobState.Completed : PrintJobState.Aborted;
        if (!result.Success) job.ErrorMessage = result.Error ?? "Windows spooler rejected print payload.";

        var jobUri = $"{printerUri}/jobs/{jobId}";

        var rb = new IppResponseBuilder(req.VersionMajor, req.VersionMinor,
            result.Success ? IppEncoding.StatusOk : IppEncoding.StatusServerError, req.RequestId);

        rb.BeginOperationAttributes()
          .AddCharset("attributes-charset", "utf-8")
          .AddNaturalLanguage("attributes-natural-language", "en");

        rb.BeginJobAttributes()
          .AddInteger("job-id", jobId)
          .AddUri("job-uri", jobUri)
          .AddEnum("job-state", (int)job.State)
          .AddKeyword("job-state-reasons", GetJobStateReason(job.State));

        return rb.Build();
    }

    private byte[] HandleValidateJob(IppRequest req, string printerName)
    {
        var rb = new IppResponseBuilder(req.VersionMajor, req.VersionMinor, IppEncoding.StatusOk, req.RequestId);
        rb.BeginOperationAttributes()
          .AddCharset("attributes-charset", "utf-8")
          .AddNaturalLanguage("attributes-natural-language", "en");
        return rb.Build();
    }

    private byte[] HandleGetJobs(IppRequest req, string printerName, string printerUri)
    {
        var rb = new IppResponseBuilder(req.VersionMajor, req.VersionMinor, IppEncoding.StatusOk, req.RequestId);
        rb.BeginOperationAttributes()
          .AddCharset("attributes-charset", "utf-8")
          .AddNaturalLanguage("attributes-natural-language", "en");

        var recentJobs = _jobs.Values
            .Where(j => j.PrinterName.Equals(printerName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.ReceivedAt)
            .Take(10);

        foreach (var j in recentJobs)
        {
            rb.BeginJobAttributes()
              .AddInteger("job-id", j.JobId)
              .AddUri("job-uri", $"{printerUri}/jobs/{j.JobId}")
              .AddName("job-name", j.DocumentName)
              .AddEnum("job-state", (int)j.State)
              .AddKeyword("job-state-reasons", GetJobStateReason(j.State));
        }

        return rb.Build();
    }

    private byte[] HandleGetJobAttributes(IppRequest req, string printerUri)
    {
        int jobId = GetJobId(req);

        if (!_jobs.TryGetValue(jobId, out var job))
            return BuildErrorResponse(req, IppEncoding.StatusClientNotFound, "Job not found.");

        var rb = new IppResponseBuilder(req.VersionMajor, req.VersionMinor, IppEncoding.StatusOk, req.RequestId);
        rb.BeginOperationAttributes()
          .AddCharset("attributes-charset", "utf-8")
          .AddNaturalLanguage("attributes-natural-language", "en");

        rb.BeginJobAttributes()
          .AddInteger("job-id", job.JobId)
          .AddUri("job-uri", $"{printerUri}/jobs/{job.JobId}")
          .AddName("job-name", job.DocumentName)
          .AddEnum("job-state", (int)job.State)
          .AddKeyword("job-state-reasons", GetJobStateReason(job.State))
          .AddText("job-state-message", job.ErrorMessage ?? string.Empty);

        return rb.Build();
    }

    private byte[] HandleCancelJob(IppRequest req)
    {
        int jobId = GetJobId(req);

        if (_jobs.TryGetValue(jobId, out var job))
            job.State = PrintJobState.Canceled;

        var rb = new IppResponseBuilder(req.VersionMajor, req.VersionMinor, IppEncoding.StatusOk, req.RequestId);
        rb.BeginOperationAttributes()
          .AddCharset("attributes-charset", "utf-8")
          .AddNaturalLanguage("attributes-natural-language", "en");
        return rb.Build();
    }

    private byte[] HandleCreateJob(IppRequest req, string printerName, string printerUri)
    {
        var jobId = Interlocked.Increment(ref _nextJobId);
        var docName = req.GetOperationAttribute("job-name") ?? "Mobile Print Document";

        var job = new PrintJob
        {
            JobId = jobId,
            PrinterName = printerName,
            DocumentName = docName,
            MimeType = req.GetOperationAttribute("document-format") ?? "application/pdf",
            State = PrintJobState.Pending
        };
        _jobs[jobId] = job;

        var jobUri = $"{printerUri}/jobs/{jobId}";

        var rb = new IppResponseBuilder(req.VersionMajor, req.VersionMinor, IppEncoding.StatusOk, req.RequestId);
        rb.BeginOperationAttributes()
          .AddCharset("attributes-charset", "utf-8")
          .AddNaturalLanguage("attributes-natural-language", "en");

        rb.BeginJobAttributes()
          .AddInteger("job-id", jobId)
          .AddUri("job-uri", jobUri)
          .AddEnum("job-state", (int)job.State)
          .AddKeyword("job-state-reasons", "none");

        return rb.Build();
    }

    private byte[] HandleSendDocument(IppRequest req, string printerName, string printerUri)
    {
        int jobId = GetJobId(req);

        if (!_jobs.TryGetValue(jobId, out var job))
            return BuildErrorResponse(req, IppEncoding.StatusClientNotFound, "Job not found.");

        var mimeType = req.GetOperationAttribute("document-format") ?? job.MimeType;
        var options = ParsePrintOptions(req);
        var isLastDocument = req.GetOperationBool("last-document") ?? true;

        lock (job)
        {
            job.MimeType = mimeType;

            if (req.DocumentData != null && req.DocumentData.Length > 0)
            {
                if (job.DocumentData.Length == 0)
                {
                    job.DocumentData = req.DocumentData;
                }
                else
                {
                    var combined = new byte[job.DocumentData.Length + req.DocumentData.Length];
                    Buffer.BlockCopy(job.DocumentData, 0, combined, 0, job.DocumentData.Length);
                    Buffer.BlockCopy(req.DocumentData, 0, combined, job.DocumentData.Length, req.DocumentData.Length);
                    job.DocumentData = combined;
                }

                job.State = PrintJobState.Processing;
            }
        }

        var success = true;
        if (isLastDocument)
        {
            var payload = job.DocumentData;
            if (payload.Length == 0)
                return BuildErrorResponse(req, IppEncoding.StatusClientBadRequest, "No document data was received.");

            JobReceived?.Invoke(job);

            var result = _printerBridge.SendToSpooler(printerName, payload, job.DocumentName, job.MimeType, options);
            success = result.Success;
            job.State = success ? PrintJobState.Completed : PrintJobState.Aborted;
            job.ErrorMessage = success ? null : result.Error ?? "Windows spooler rejected print payload.";
        }

        var jobUri = $"{printerUri}/jobs/{jobId}";

        var rb = new IppResponseBuilder(req.VersionMajor, req.VersionMinor,
            success ? IppEncoding.StatusOk : IppEncoding.StatusServerError, req.RequestId);
        rb.BeginOperationAttributes()
          .AddCharset("attributes-charset", "utf-8")
          .AddNaturalLanguage("attributes-natural-language", "en");

        rb.BeginJobAttributes()
          .AddInteger("job-id", job.JobId)
          .AddUri("job-uri", jobUri)
          .AddEnum("job-state", (int)job.State)
          .AddKeyword("job-state-reasons", GetJobStateReason(job.State));

        return rb.Build();
    }

    private string ResolvePrinterUri(string printerName, string? requestedPrinterUri)
    {
        if (!string.IsNullOrWhiteSpace(requestedPrinterUri))
            return requestedPrinterUri.TrimEnd('/');

        var cfg = FindPrinter(printerName);
        var queuePath = cfg != null ? PrinterIdentity.GetQueuePath(cfg) : printerName.Replace(' ', '_');
        return $"ipp://{NetworkHelper.GetLocalIPv4Address()}:{_settings.Current.IppPort}/printers/{Uri.EscapeDataString(queuePath)}";
    }

    private static string ExtractQueueFromUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var text = value.Trim();
        var jobsIndex = text.IndexOf("/jobs/", StringComparison.OrdinalIgnoreCase);
        if (jobsIndex >= 0)
            text = text[..jobsIndex];

        text = text.TrimEnd('/');
        var slash = text.LastIndexOf('/');
        if (slash >= 0 && slash < text.Length - 1)
            text = text[(slash + 1)..];

        return SafeUnescape(text);
    }

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch
        {
            return value;
        }
    }

    private static int GetJobId(IppRequest req)
    {
        var id = req.GetOperationInt("job-id");
        if (id.HasValue) return id.Value;

        var uri = req.GetOperationAttribute("job-uri");
        if (!string.IsNullOrWhiteSpace(uri))
        {
            var slash = uri.LastIndexOf('/');
            if (slash >= 0 && int.TryParse(uri[(slash + 1)..], out var parsed))
                return parsed;
        }

        return 0;
    }

    private static PrintOptions ParsePrintOptions(IppRequest req)
    {
        var options = new PrintOptions();

        var copies = req.GetOperationInt("copies");
        if (copies is > 0)
            options.Copies = Math.Clamp(copies.Value, 1, 99);

        var sides = req.GetOperationAttribute("sides");
        options.Duplex = !string.IsNullOrWhiteSpace(sides)
                         && sides.StartsWith("two-sided", StringComparison.OrdinalIgnoreCase);

        var colorMode = req.GetOperationAttribute("print-color-mode");
        if (!string.IsNullOrWhiteSpace(colorMode))
        {
            if (colorMode.Contains("monochrome", StringComparison.OrdinalIgnoreCase))
                options.Color = false;
            else if (colorMode.Contains("color", StringComparison.OrdinalIgnoreCase))
                options.Color = true;
        }

        options.Quality = req.GetOperationInt("print-quality");
        return options;
    }

    private static string GetJobStateReason(PrintJobState state) => state switch
    {
        PrintJobState.Completed => "job-completed-successfully",
        PrintJobState.Aborted => "aborted-by-system",
        PrintJobState.Canceled => "job-canceled-by-user",
        _ => "none"
    };

    private PrinterConfig? FindPrinter(string? printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            return _settings.Current.Printers.FirstOrDefault(p => p.Enabled)
                ?? _settings.Current.Printers.FirstOrDefault();

        var decoded = SafeUnescape(printerName).Trim();
        var normalized = decoded.Replace('_', ' ');
        var key = NormalizeQueueKey(decoded);

        return _settings.Current.Printers.FirstOrDefault(p =>
            p.WindowsPrinterName.Equals(decoded, StringComparison.OrdinalIgnoreCase)
            || p.DisplayName.Equals(decoded, StringComparison.OrdinalIgnoreCase)
            || p.WindowsPrinterName.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || p.DisplayName.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?? _settings.Current.Printers.FirstOrDefault(p =>
                NormalizeQueueKey(p.WindowsPrinterName) == key
                || NormalizeQueueKey(p.DisplayName) == key)
            ?? _settings.Current.Printers.FirstOrDefault(p => p.Enabled)
            ?? _settings.Current.Printers.FirstOrDefault();
    }

    private static string NormalizeQueueKey(string value) =>
        new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static byte[] BuildErrorResponse(IppRequest req, ushort status, string message)
    {
        var rb = new IppResponseBuilder(req.VersionMajor, req.VersionMinor, status, req.RequestId);
        rb.BeginOperationAttributes()
          .AddCharset("attributes-charset", "utf-8")
          .AddNaturalLanguage("attributes-natural-language", "en")
          .AddText("status-message", message);
        return rb.Build();
    }

    private static byte[] BuildMinimalErrorResponse()
    {
        return new IppResponseBuilder(1, 1, IppEncoding.StatusServerError, 0)
            .BeginOperationAttributes()
            .AddCharset("attributes-charset", "utf-8")
            .AddNaturalLanguage("attributes-natural-language", "en")
            .Build();
    }
}
