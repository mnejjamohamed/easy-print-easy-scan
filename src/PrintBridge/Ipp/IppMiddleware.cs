using Microsoft.AspNetCore.Http;
using PrintBridge.Models;
using PrintBridge.Network;
using PrintBridge.Printing;
using PrintBridge.Scanning;
using PrintBridge.Settings;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace PrintBridge.Ipp;

public sealed class IppMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IppRequestHandler _handler;
    private readonly SettingsManager _settings;

    public IppMiddleware(RequestDelegate next, IppRequestHandler handler, SettingsManager settings)
    {
        _next = next;
        _handler = handler;
        _settings = settings;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (path.Equals("/api/scan", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "GET")
        {
            await HandleScanAsync(context);
            return;
        }

        if (path.Equals("/api/scanners", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "GET")
        {
            var scanners = WiaScannerHelper.GetScannerNames();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                available = scanners.Count > 0,
                scanners
            }));
            return;
        }

        if (path.Equals("/api/preview", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "POST")
        {
            await HandlePreviewAsync(context);
            return;
        }

        if (path.Equals("/api/print-file", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "POST")
        {
            await HandlePrintFileAsync(context);
            return;
        }

        if (path.Equals("/api/jobs", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "GET")
        {
            var jobs = _handler.GetAllJobs().Select(j => new
            {
                j.JobId,
                j.PrinterName,
                j.DocumentName,
                j.MimeType,
                Size = j.FriendlySize,
                State = j.FriendlyState,
                StateCode = (int)j.State,
                Time = j.ReceivedAt.ToLocalTime().ToString("hh:mm:ss tt"),
                j.ErrorMessage
            });

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(jobs));
            return;
        }

        if (path.Equals("/api/clear-jobs", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "POST")
        {
            _handler.ClearJobs();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = true }));
            return;
        }

        if (context.Request.Method == "POST")
        {
            string printerName = string.Empty;
            var trimmed = path.Trim('/');

            if (trimmed.StartsWith("printers/", StringComparison.OrdinalIgnoreCase))
            {
                printerName = trimmed.Substring(9);
            }
            else if (trimmed.StartsWith("ipp/printers/", StringComparison.OrdinalIgnoreCase))
            {
                printerName = trimmed.Substring(13);
            }
            else if (!string.IsNullOrEmpty(trimmed)
                     && !trimmed.Equals("ipp", StringComparison.OrdinalIgnoreCase)
                     && !trimmed.Equals("print", StringComparison.OrdinalIgnoreCase))
            {
                printerName = trimmed;
            }

            var jobsIndex = printerName.IndexOf("/jobs/", StringComparison.OrdinalIgnoreCase);
            if (jobsIndex >= 0)
                printerName = printerName[..jobsIndex];

            using var ms = new MemoryStream();
            await context.Request.Body.CopyToAsync(ms);
            var requestBytes = ms.ToArray();
            var requestedPrinterUri = BuildRequestPrinterUri(context);

            var responseBytes = _handler.HandleRequest(requestBytes, printerName, requestedPrinterUri);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/ipp";
            context.Response.ContentLength = responseBytes.Length;
            await context.Response.Body.WriteAsync(responseBytes);
            return;
        }

        if (context.Request.Method == "GET")
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(RenderDashboardHtml());
            return;
        }

        await _next(context);
    }

    private async Task HandleScanAsync(HttpContext context)
    {
        var query = context.Request.Query;
        var requestedFormat = query["format"].ToString().ToLowerInvariant();
        if (requestedFormat is not ("jpeg" or "jpg" or "png" or "pdf"))
            requestedFormat = "jpeg";

        var options = new ScanOptions
        {
            Dpi = ParseInt(query["dpi"].ToString(), 300),
            Color = !string.Equals(query["color"].ToString(), "gray", StringComparison.OrdinalIgnoreCase),
            UseAdf = string.Equals(query["source"].ToString(), "adf", StringComparison.OrdinalIgnoreCase),
            DeviceName = query["device"].ToString(),
            Format = requestedFormat == "png" ? "png" : "jpeg"
        };

        var scan = WiaScannerHelper.ScanDocument(options);
        if (!scan.Success || scan.PageCount == 0 || scan.Data == null || scan.Data.Length == 0)
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                success = false,
                error = scan.Error ?? "Unable to acquire an image from the scanner."
            }));
            return;
        }

        var pageCount = scan.PageCount;
        context.Response.Headers["X-Scan-Page-Count"] = pageCount.ToString(CultureInfo.InvariantCulture);

        if (requestedFormat == "pdf")
        {
            var pdf = ScanPdfBuilder.CreatePdf(scan.Pages, options.Dpi);
            if (pdf == null)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "The scanned image could not be converted to PDF."
                }));
                return;
            }

            context.Response.ContentType = "application/pdf";
            context.Response.ContentLength = pdf.Length;
            await context.Response.Body.WriteAsync(pdf);
            return;
        }

        context.Response.ContentType = scan.ContentType;
        context.Response.ContentLength = scan.Data.Length;
        await context.Response.Body.WriteAsync(scan.Data);
    }

    private async Task HandlePreviewAsync(HttpContext context)
    {
        try
        {
            var form = await context.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault(f => f.Length > 0);

            if (file == null)
            {
                context.Response.StatusCode = 400;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "No file was uploaded."
                }));
                return;
            }

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);

            var data = ms.ToArray();
            var mimeType = ResolveMimeType(file.FileName, file.ContentType);
            var result = await Task.Run(() => DocumentPreview.CreateAsync(data, file.FileName, mimeType));

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                success = true,
                kind = result.Kind,
                fileName = result.FileName,
                pageCount = result.PageCount,
                pages = result.Pages,
                text = result.Text,
                message = result.Message
            }));
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message
            }));
        }
    }

    private async Task HandlePrintFileAsync(HttpContext context)
    {
        try
        {
            var form = await context.Request.ReadFormAsync();
            var files = form.Files.Where(f => f.Length > 0).ToList();
            var printerName = form["printer"].ToString();

            var colorValue = form["color"].ToString().ToLowerInvariant();
            var pagesPerSheet = ParseInt(form["pagesPerSheet"].ToString(), 1);
            var options = new PrintOptions
            {
                Copies = Math.Clamp(ParseInt(form["copies"].ToString(), 1), 1, 99),
                Duplex = string.Equals(form["duplex"].ToString(), "duplex", StringComparison.OrdinalIgnoreCase),
                Color = colorValue switch
                {
                    "mono" or "monochrome" or "grayscale" => false,
                    "color" => true,
                    _ => null
                },
                PagesPerSheet = pagesPerSheet is 2 or 4 or 6 or 9 or 16 ? pagesPerSheet : 1
            };

            if (files.Count == 0)
            {
                context.Response.StatusCode = 400;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "No file was uploaded."
                }));
                return;
            }

            var results = new List<object>();
            var errors = new List<string>();
            int printed = 0;

            foreach (var file in files)
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                var data = ms.ToArray();
                var mimeType = ResolveMimeType(file.FileName, file.ContentType);

                var job = _handler.SubmitDirectJob(printerName, file.FileName, mimeType, data, options);
                var ok = job.State == PrintJobState.Completed;
                if (ok)
                    printed++;
                else
                    errors.Add($"{file.FileName}: {job.ErrorMessage ?? "unknown error"}");

                results.Add(new
                {
                    fileName = file.FileName,
                    success = ok,
                    jobId = job.JobId,
                    printer = job.PrinterName,
                    message = ok
                        ? $"Job #{job.JobId} sent to '{job.PrinterName}'."
                        : $"Job #{job.JobId} failed: {job.ErrorMessage ?? "unknown error"}",
                    error = job.ErrorMessage
                });
            }

            var success = printed == files.Count;
            var message = success
                ? (files.Count == 1
                    ? "Document sent to the printer successfully."
                    : $"{printed} document(s) sent to the printer.")
                : $"{printed} of {files.Count} document(s) printed. {string.Join(" ", errors)}";

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                success,
                printed,
                total = files.Count,
                message,
                jobs = results
            }));
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message
            }));
        }
    }

    private static string BuildRequestPrinterUri(HttpContext context)
    {
        var host = context.Request.Host.Value;
        if (string.IsNullOrWhiteSpace(host))
        {
            var localAddress = context.Connection.LocalIpAddress?.ToString() ?? "127.0.0.1";
            host = $"{localAddress}:{context.Connection.LocalPort}";
        }

        var encodedPath = context.Request.PathBase.Add(context.Request.Path).ToUriComponent();
        var jobsIndex = encodedPath.IndexOf("/jobs/", StringComparison.OrdinalIgnoreCase);
        if (jobsIndex >= 0)
            encodedPath = encodedPath[..jobsIndex];

        var scheme = context.Request.IsHttps ? "ipps" : "ipp";
        return $"{scheme}://{host}{encodedPath}";
    }

    private static string ResolveMimeType(string fileName, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType)
            && !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return contentType;
        }

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".tif" or ".tiff" => "image/tiff",
            ".ico" => "image/x-icon",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".txt" or ".log" or ".ini" or ".config" => "text/plain",
            ".csv" => "text/csv",
            ".tsv" => "text/tab-separated-values",
            ".md" or ".markdown" => "text/markdown",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".html" or ".htm" => "text/html",
            ".rtf" => "application/rtf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".odt" => "application/vnd.oasis.opendocument.text",
            ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
            ".odp" => "application/vnd.oasis.opendocument.presentation",
            ".ps" => "application/postscript",
            ".pcl" or ".prn" => "application/vnd.hp-pcl",
            _ => "application/octet-stream"
        };
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var result) ? result : fallback;

    private string RenderDashboardHtml()
    {
        var ip = NetworkHelper.GetLocalIPv4Address();
        var port = _settings.Current.IppPort;
        var printers = _settings.Current.Printers;
        var scanners = WiaScannerHelper.GetScannerNames();
        var hasScanner = scanners.Count > 0;

        var printerOptions = string.Join("", printers.Where(p => p.Enabled).Select(p =>
        {
            var name = string.IsNullOrWhiteSpace(p.DisplayName) ? p.WindowsPrinterName : p.DisplayName;
            return $"<option value=\"{WebUtility.HtmlEncode(p.WindowsPrinterName)}\">{WebUtility.HtmlEncode(name)} ({WebUtility.HtmlEncode(p.WindowsPrinterName)})</option>";
        }));

        if (string.IsNullOrEmpty(printerOptions))
            printerOptions = "<option value=\"\">No shared printer enabled</option>";

        var scannerOptions = string.Join("", scanners.Select(s =>
            $"<option value=\"{WebUtility.HtmlEncode(s)}\">{WebUtility.HtmlEncode(s)}</option>"));

        if (string.IsNullOrEmpty(scannerOptions))
            scannerOptions = "<option value=\"\">No scanner detected</option>";

        var printerRows = string.Join("", printers.Select(p =>
        {
            var name = string.IsNullOrWhiteSpace(p.DisplayName) ? p.WindowsPrinterName : p.DisplayName;
            return $"""
                <tr>
                  <td><strong>{WebUtility.HtmlEncode(name)}</strong><br><small class="muted">{WebUtility.HtmlEncode(p.WindowsPrinterName)}</small></td>
                  <td>{(p.Enabled ? "<span class='badge badge-success'><span class='dot'></span> Shared</span>" : "<span class='badge badge-gray'>Disabled</span>")}</td>
                  <td>{(p.Color ? "Color" : "Mono")}</td>
                  <td>{(p.Duplex ? "Duplex" : "Single")}</td>
                </tr>
                """;
        }));

        var scannerWarning = hasScanner
            ? ""
            : "<div class='alert alert-error' style='display:block;'>No WIA scanner was detected. Connect the printer/scanner by USB and install its scanner driver.</div>";

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Easy Print Easy Scan — Mobile & Web Print Hub</title>
              <script>
                (function () {
                  try {
                    var saved = localStorage.getItem('epes-theme');
                    var theme = saved || (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
                    document.documentElement.setAttribute('data-theme', theme);
                  } catch (e) {
                    document.documentElement.setAttribute('data-theme', 'light');
                  }
                })();
              </script>
              <style>
                :root {
                  --primary: #ccd5ae;
                  --primary-hover: #c2cd9e;
                  --primary-strong: #5f6b45;
                  --primary-soft: #e9edc9;
                  --on-primary: #313a24;
                  --bg: #faf9f5;
                  --card: #ffffff;
                  --text: #1f1e1c;
                  --muted: #6b6a63;
                  --border: #e6e3d8;
                  --subtle: #f5f4ea;
                  --input-bg: #ffffff;
                  --success: #4d6b3f;
                  --success-soft: #e9edc9;
                  --danger: #a33a2f;
                  --danger-soft: #f6e5e0;
                  --warning: #8a6a2f;
                  --warning-soft: #f4ecd8;
                  --overlay: rgba(31, 30, 28, 0.45);
                  --shadow: 0 1px 2px rgba(31, 30, 28, 0.04);
                }
                [data-theme="dark"] {
                  --primary: #ccd5ae;
                  --primary-hover: #e9edc9;
                  --primary-strong: #e9edc9;
                  --primary-soft: #2b3220;
                  --on-primary: #262d1c;
                  --bg: #151513;
                  --card: #1e1e1b;
                  --text: #f0eee6;
                  --muted: #a3a19a;
                  --border: #35342f;
                  --subtle: #1a1a18;
                  --input-bg: #24241f;
                  --success: #ccd5ae;
                  --success-soft: #2b3220;
                  --danger: #f0a8a0;
                  --danger-soft: #3a2320;
                  --warning: #e3c47a;
                  --warning-soft: #38301d;
                  --overlay: rgba(0, 0, 0, 0.6);
                  --shadow: none;
                }
                * { box-sizing: border-box; }
                body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background: var(--bg); color: var(--text); margin: 0; padding: 32px 20px 48px; line-height: 1.6; transition: background 0.2s, color 0.2s; }
                .container { max-width: 980px; margin: 0 auto; }
                header { display: flex; justify-content: space-between; align-items: flex-start; gap: 16px; flex-wrap: wrap; margin-bottom: 28px; padding-bottom: 20px; border-bottom: 1px solid var(--border); }
                h1, .card-title, .modal-title { font-family: "Copernicus", "Tiempos Text", Georgia, "Times New Roman", serif; font-weight: 500; letter-spacing: -0.02em; }
                h1 { margin: 0; font-size: 30px; line-height: 1.15; color: var(--text); display: flex; align-items: center; gap: 10px; }
                h1 .icon { width: 26px; height: 26px; }
                .muted { color: var(--muted); }
                .card { background: var(--card); border-radius: 16px; border: 1px solid var(--border); box-shadow: var(--shadow); padding: 28px; margin-bottom: 20px; }
                .card-title { margin: 0 0 6px 0; font-size: 21px; display: flex; align-items: center; gap: 9px; }
                .card-title .icon { width: 20px; height: 20px; color: var(--primary-strong); }
                .card > .muted { margin-bottom: 18px; }
                .badge { display: inline-flex; align-items: center; padding: 4px 11px; border-radius: 9999px; font-size: 12px; font-weight: 600; gap: 6px; }
                .badge .dot { width: 7px; height: 7px; border-radius: 50%; background: currentColor; }
                .badge-success { background: var(--success-soft); color: var(--success); }
                .badge-gray { background: var(--subtle); color: var(--muted); }
                .badge-error { background: var(--danger-soft); color: var(--danger); }
                .badge-warn { background: var(--warning-soft); color: var(--warning); }
                .btn { display: inline-flex; align-items: center; justify-content: center; gap: 8px; background: var(--primary); color: var(--on-primary); border: 1px solid transparent; padding: 10px 18px; border-radius: 9px; font-family: inherit; font-size: 14px; font-weight: 600; cursor: pointer; text-decoration: none; transition: background 0.15s, border-color 0.15s, color 0.15s; }
                .btn:hover { background: var(--primary-hover); }
                .btn:disabled { opacity: 0.5; cursor: not-allowed; }
                .btn-secondary { background: transparent; color: var(--text); border-color: var(--border); }
                .btn-secondary:hover { background: var(--primary-soft); border-color: var(--primary); }
                .btn-danger { background: transparent; color: var(--danger); border-color: var(--danger); }
                .btn-danger:hover { background: var(--danger-soft); }
                .btn-sm { padding: 6px 12px; font-size: 12.5px; }
                .icon { width: 18px; height: 18px; flex-shrink: 0; fill: none; stroke: currentColor; stroke-width: 1.6; stroke-linecap: round; stroke-linejoin: round; vertical-align: -0.18em; }
                .grid-2 { display: grid; grid-template-columns: 1fr 1fr; gap: 20px; }
                @media (max-width: 700px) { .grid-2 { grid-template-columns: 1fr; } }
                .table-wrap { overflow-x: auto; }
                table { width: 100%; border-collapse: collapse; margin-top: 12px; }
                th, td { text-align: left; padding: 11px 12px; border-bottom: 1px solid var(--border); font-size: 14px; vertical-align: top; }
                th { color: var(--muted); font-size: 11.5px; text-transform: uppercase; letter-spacing: 0.06em; white-space: nowrap; background: var(--subtle); }
                tr:last-child td { border-bottom: 0; }
                .upload-zone { border: 1.5px dashed var(--border); border-radius: 12px; padding: 26px; text-align: center; background: var(--subtle); cursor: pointer; margin-bottom: 16px; transition: border-color 0.15s, background 0.15s; }
                .upload-zone:hover { border-color: var(--primary-strong); background: var(--primary-soft); }
                .upload-zone .icon { width: 26px; height: 26px; color: var(--primary-strong); margin-bottom: 6px; }
                label.field { font-size: 12px; font-weight: 600; color: var(--muted); display: block; margin-bottom: 6px; text-transform: uppercase; letter-spacing: 0.04em; }
                select, input[type="text"], input[type="number"] { width: 100%; padding: 10px 12px; border: 1px solid var(--border); border-radius: 8px; font-family: inherit; font-size: 14px; margin-bottom: 14px; background: var(--input-bg); color: var(--text); }
                select:focus, input:focus { outline: 2px solid var(--primary-soft); outline-offset: 1px; border-color: var(--primary-strong); }
                .form-row { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
                @media (max-width: 520px) { .form-row { grid-template-columns: 1fr; } }
                .preview-box { display: none; text-align: center; margin-top: 16px; padding: 18px; background: var(--subtle); border-radius: 12px; border: 1px solid var(--border); }
                .preview-img { max-width: 100%; max-height: 420px; border-radius: 8px; box-shadow: var(--shadow); margin-bottom: 14px; }
                .file-list { list-style: none; padding: 0; margin: 0 0 14px 0; }
                .file-list li { display: flex; justify-content: space-between; gap: 10px; padding: 9px 11px; background: var(--subtle); border: 1px solid var(--border); border-radius: 8px; margin-bottom: 6px; font-size: 13px; }
                .spinner { display: none; margin: 16px auto; width: 28px; height: 28px; border: 2px solid var(--border); border-top-color: var(--primary-strong); border-radius: 50%; animation: spin 0.9s linear infinite; }
                @keyframes spin { 0% { transform: rotate(0deg); } 100% { transform: rotate(360deg); } }
                .alert { padding: 12px 16px; border-radius: 10px; margin-bottom: 16px; font-size: 14px; display: none; }
                .alert-success { background: var(--success-soft); color: var(--success); border: 1px solid var(--primary); }
                .alert-error { background: var(--danger-soft); color: var(--danger); border: 1px solid var(--danger); }
                .modal { display: none; position: fixed; inset: 0; background: var(--overlay); z-index: 1000; padding: 16px; }
                .modal.open { display: flex; align-items: center; justify-content: center; }
                .modal-content { background: var(--card); border: 1px solid var(--border); border-radius: 16px; width: 100%; max-width: 880px; max-height: 92vh; display: flex; flex-direction: column; overflow: hidden; }
                .modal-header { display: flex; justify-content: space-between; align-items: center; gap: 10px; padding: 16px 20px; border-bottom: 1px solid var(--border); }
                .modal-title { font-size: 17px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; margin: 0; }
                .modal-body { padding: 18px; overflow: auto; text-align: center; background: var(--subtle); flex: 1; }
                .modal-body img { max-width: 100%; max-height: 62vh; border-radius: 8px; box-shadow: var(--shadow); }
                .modal-body iframe { width: 100%; height: 62vh; border: 0; border-radius: 8px; background: white; }
                .modal-body pre { text-align: left; white-space: pre-wrap; word-break: break-word; background: var(--input-bg); color: var(--text); border: 1px solid var(--border); border-radius: 8px; padding: 14px; max-height: 62vh; overflow: auto; font-size: 13px; }
                .preview-note { font-size: 13px; color: var(--muted); margin: 0 0 12px; }
                .preview-sheet { background: #ffffff; border: 1px solid var(--border); border-radius: 4px; box-shadow: var(--shadow); margin: 0 auto 16px; width: 100%; max-width: 640px; aspect-ratio: 8.5 / 11; padding: 10px; }
                .preview-sheet.landscape { aspect-ratio: 11 / 8.5; max-width: 760px; }
                .preview-sheet-grid { display: grid; gap: 8px; width: 100%; height: 100%; }
                .preview-sheet-grid img { width: 100%; height: 100%; object-fit: contain; background: #ffffff; border: 1px solid #e6e3d8; border-radius: 2px; box-shadow: none; }
                .modal-empty { padding: 40px 16px; color: var(--muted); font-size: 14px; }
                .modal-footer { display: flex; justify-content: space-between; align-items: center; gap: 10px; padding: 14px 20px; border-top: 1px solid var(--border); }
                a { color: var(--primary-strong); }
                .theme-toggle { white-space: nowrap; }
              </style>
            </head>
            <body>
              <div class="container">
                <header>
                  <div>
                    <h1>
                      <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M6 9V3h12v6"/><path d="M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2"/><rect x="6" y="14" width="12" height="8" rx="1"/></svg>
                      Easy Print Easy Scan
                    </h1>
                    <div class="muted" style="font-size: 14px;">AirPrint & Wi-Fi Gateway for iPhone, iPad, Android & Web</div>
                  </div>
                  <div style="display: flex; align-items: center; gap: 8px; flex-wrap: wrap;">
                    <button class="btn btn-secondary btn-sm theme-toggle" id="themeToggle" type="button" onclick="toggleTheme()" aria-label="Toggle dark and light theme">
                      <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8Z"/></svg>
                      Dark
                    </button>
                    <div class="badge badge-success"><span class="dot"></span> Server Online ({{WebUtility.HtmlEncode(ip)}}:{{port}})</div>
                  </div>
                </header>

                <div id="statusAlert" class="alert"></div>

                <div class="grid-2">
                  <div class="card">
                    <h2 class="card-title">
                      <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M3 7V5a2 2 0 0 1 2-2h2"/><path d="M17 3h2a2 2 0 0 1 2 2v2"/><path d="M21 17v2a2 2 0 0 1-2 2h-2"/><path d="M7 21H5a2 2 0 0 1-2-2v-2"/><path d="M7 12h10"/></svg>
                      Document Scanner
                    </h2>
                    <p class="muted" style="font-size: 14px; margin-top: 0;">
                      Scan paper straight into your browser, download it, or print a copy.
                    </p>

                    {{scannerWarning}}

                    <label class="field">Scanner</label>
                    <select id="selectScanner">{{scannerOptions}}</select>

                    <div class="form-row">
                      <div>
                        <label class="field">Source</label>
                        <select id="scanSource">
                          <option value="flatbed">Flatbed</option>
                          <option value="adf">Document feeder (ADF)</option>
                        </select>
                      </div>
                      <div>
                        <label class="field">Color</label>
                        <select id="scanColor">
                          <option value="color">Color</option>
                          <option value="gray">Grayscale</option>
                        </select>
                      </div>
                    </div>

                    <div class="form-row">
                      <div>
                        <label class="field">Resolution</label>
                        <select id="scanDpi">
                          <option value="150">150 DPI (draft)</option>
                          <option value="300" selected>300 DPI (recommended)</option>
                          <option value="600">600 DPI (high quality)</option>
                        </select>
                      </div>
                      <div>
                        <label class="field">Output format</label>
                        <select id="scanFormat">
                          <option value="jpeg">JPEG image</option>
                          <option value="png">PNG image</option>
                          <option value="pdf">PDF document</option>
                        </select>
                      </div>
                    </div>

                    <p class="muted" id="adfScanHint" style="font-size: 12px; margin: 4px 0 12px; display: none;">
                      Tip: choose PDF to capture every page in the feeder into one document.
                    </p>

                    <div style="display: flex; gap: 10px;">
                      <button class="btn" id="btnScan" style="flex: 1;" onclick="triggerScan()" {{(hasScanner ? "" : "disabled")}}>
                        <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M3 7V5a2 2 0 0 1 2-2h2"/><path d="M17 3h2a2 2 0 0 1 2 2v2"/><path d="M21 17v2a2 2 0 0 1-2 2h-2"/><path d="M7 21H5a2 2 0 0 1-2-2v-2"/><path d="M7 12h10"/></svg>
                        Scan Document
                      </button>
                      <button class="btn btn-danger" id="btnCancelScan" type="button" style="flex: 1; display: none;" onclick="cancelScan()">
                        <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M18 6 6 18M6 6l12 12"/></svg>
                        Cancel Scan
                      </button>
                    </div>

                    <div class="spinner" id="scanSpinner"></div>

                      <div class="preview-box" id="scanPreviewBox">
                      <img id="scanPreviewImg" class="preview-img" src="" alt="Scanned Document">
                      <div id="scanPdfBox" style="display:none; margin-bottom: 14px; font-weight: 600;">
                        <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/></svg>
                        PDF document ready<span id="scanPageCount"></span>
                      </div>
                      <div style="display: flex; gap: 10px; justify-content: center; flex-wrap: wrap;">
                        <a id="btnDownloadScan" class="btn btn-secondary" download="Scanned_Document.jpg" href="#">
                          <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="M7 10l5 5 5-5"/><path d="M12 15V3"/></svg>
                          Download
                        </a>
                        <button class="btn" onclick="printScannedImage()">
                          <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M6 9V3h12v6"/><path d="M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2"/><rect x="6" y="14" width="12" height="8" rx="1"/></svg>
                          Print Copy
                        </button>
                      </div>
                    </div>
                  </div>

                  <div class="card">
                    <h2 class="card-title">
                      <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="M17 8l-5-5-5 5"/><path d="M12 3v12"/></svg>
                      Quick File Print
                    </h2>
                    <p class="muted" style="font-size: 14px; margin-top: 0;">
                      Print PDF, photos, text, Word, Excel, PowerPoint and OpenDocument files from any device.
                    </p>

                    <label class="field">Target printer</label>
                    <select id="selectPrinter">{{printerOptions}}</select>

                    <div class="form-row">
                      <div>
                        <label class="field">Copies</label>
                        <input type="number" id="printCopies" min="1" max="99" value="1">
                      </div>
                      <div>
                        <label class="field">Color</label>
                        <select id="printColor">
                          <option value="color" selected>Color</option>
                          <option value="mono">Monochrome</option>
                        </select>
                      </div>
                    </div>

                    <div class="form-row">
                      <div>
                        <label class="field">Sides</label>
                        <select id="printDuplex">
                          <option value="single" selected>Single-sided</option>
                          <option value="duplex">Double-sided</option>
                        </select>
                      </div>
                      <div>
                        <label class="field">Pages per side</label>
                        <select id="printPagesPerSheet">
                          <option value="1" selected>1 page</option>
                          <option value="2">2 pages</option>
                          <option value="4">4 pages</option>
                          <option value="6">6 pages</option>
                          <option value="9">9 pages</option>
                          <option value="16">16 pages</option>
                        </select>
                      </div>
                    </div>

                    <div class="upload-zone" onclick="document.getElementById('fileUploadInput').click()">
                      <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/></svg>
                      <div id="fileUploadName" style="font-weight: 500; font-size: 15px;">Tap to select files</div>
                      <div class="muted" style="font-size: 13px; margin-top: 4px;">
                        PDF, JPG, PNG, BMP, GIF, TIFF, TXT, CSV, HTML, RTF, DOCX, XLSX, PPTX, ODT, ODS, ODP
                      </div>
                      <input type="file" id="fileUploadInput" multiple accept=".pdf,.jpg,.jpeg,.png,.bmp,.gif,.tif,.tiff,.txt,.log,.csv,.tsv,.md,.json,.xml,.html,.htm,.rtf,.docx,.xlsx,.pptx,.odt,.ods,.odp,.ps,.pcl" style="display:none;" onchange="handleFileSelected(this)">
                    </div>

                    <ul class="file-list" id="fileList"></ul>

                    <div style="display: flex; gap: 10px; flex-wrap: wrap;">
                      <button class="btn btn-secondary" id="btnPreviewFile" style="flex: 1;" onclick="openPreview()" disabled>
                        <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/></svg>
                        Preview
                      </button>
                      <button class="btn" id="btnPrintFile" style="flex: 2;" onclick="submitPrintFile()" disabled>
                        <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M6 9V3h12v6"/><path d="M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2"/><rect x="6" y="14" width="12" height="8" rx="1"/></svg>
                        Print Document(s)
                      </button>
                      <button class="btn btn-danger" id="btnCancelPrint" type="button" style="flex: 1; display: none;" onclick="cancelPrint()">
                        <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M18 6 6 18M6 6l12 12"/></svg>
                        Cancel Print
                      </button>
                    </div>
                  </div>
                </div>

                <div class="card">
                  <h2 class="card-title">
                    <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M6 9V3h12v6"/><path d="M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2"/><rect x="6" y="14" width="12" height="8" rx="1"/></svg>
                    Shared Printers
                  </h2>
                  <div class="table-wrap">
                    <table>
                      <thead>
                        <tr><th>Printer Name</th><th>Status</th><th>Color</th><th>Duplex</th></tr>
                      </thead>
                      <tbody>
                        {{printerRows}}
                      </tbody>
                    </table>
                  </div>
                </div>

                <div class="card">
                  <div style="display: flex; justify-content: space-between; align-items: center; gap: 10px; flex-wrap: wrap; margin-bottom: 12px;">
                    <h2 class="card-title" style="margin: 0;">
                      <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><rect x="8" y="2" width="8" height="4" rx="1"/><path d="M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2"/><path d="M8 11h8M8 15h6"/></svg>
                      Recent Print Jobs
                    </h2>
                    <div style="display: flex; gap: 8px;">
                      <button class="btn btn-secondary btn-sm" onclick="loadJobs()">
                        <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M21 12a9 9 0 1 1-2.6-6.4"/><path d="M21 3v6h-6"/></svg>
                        Refresh
                      </button>
                      <button class="btn btn-danger btn-sm" onclick="clearJobs()">
                        <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M3 6h18"/><path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/></svg>
                        Clear History
                      </button>
                    </div>
                  </div>

                  <div class="table-wrap">
                    <table id="jobsTable">
                      <thead>
                        <tr><th>#</th><th>Document</th><th>Printer</th><th>Format</th><th>Size</th><th>Status</th><th>Time</th><th>Details</th></tr>
                      </thead>
                      <tbody id="jobsBody">
                        <tr><td colspan="8" style="text-align:center;" class="muted">Loading jobs...</td></tr>
                      </tbody>
                    </table>
                  </div>
                </div>
              </div>

              <div class="modal" id="previewModal" onclick="if (event.target === this) closePreview()">
                <div class="modal-content">
                  <div class="modal-header">
                    <div class="modal-title" id="previewTitle">Preview</div>
                    <button class="btn btn-secondary btn-sm" onclick="closePreview()">
                      <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M18 6 6 18M6 6l12 12"/></svg>
                      Close
                    </button>
                  </div>
                  <div class="modal-body" id="previewBody"></div>
                  <div class="modal-footer">
                    <button class="btn btn-secondary btn-sm" id="btnPreviewPrev" onclick="changePreview(-1)">
                      <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="m15 18-6-6 6-6"/></svg>
                      Previous
                    </button>
                    <span class="muted" id="previewCounter" style="font-size: 13px;"></span>
                    <button class="btn btn-secondary btn-sm" id="btnPreviewNext" onclick="changePreview(1)">
                      Next
                      <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="m9 18 6-6-6-6"/></svg>
                    </button>
                  </div>
                </div>
              </div>

              <script>
                let currentScannedBlob = null;
                let currentScannedName = 'Scanned_Document.jpg';
                let selectedFiles = [];
                let previewIndex = 0;
                let previewToken = 0;
                let lastPreviewData = null;
                let scanController = null;
                let printController = null;

                const ICON_MOON = '<svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8Z"/></svg>';
                const ICON_SUN = '<svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/></svg>';
                const ICON_X = '<svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M18 6 6 18M6 6l12 12"/></svg>';
                const ICON_PRINTER = '<svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M6 9V3h12v6"/><path d="M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2"/><rect x="6" y="14" width="12" height="8" rx="1"/></svg>';

                function applyTheme(theme) {
                  const value = theme === 'dark' ? 'dark' : 'light';
                  document.documentElement.setAttribute('data-theme', value);
                  const btn = document.getElementById('themeToggle');
                  if (btn) {
                    btn.innerHTML = (value === 'dark' ? ICON_SUN + ' Light' : ICON_MOON + ' Dark');
                    btn.setAttribute('aria-pressed', value === 'dark' ? 'true' : 'false');
                  }
                  try { localStorage.setItem('epes-theme', value); } catch (e) {}
                }

                function toggleTheme() {
                  const current = document.documentElement.getAttribute('data-theme') === 'dark' ? 'dark' : 'light';
                  applyTheme(current === 'dark' ? 'light' : 'dark');
                }

                applyTheme(document.documentElement.getAttribute('data-theme') || 'light');

                function openPreview() {
                  if (selectedFiles.length === 0) return;
                  previewIndex = 0;
                  document.getElementById('previewModal').classList.add('open');
                  renderPreview();
                }

                function closePreview() {
                  document.getElementById('previewModal').classList.remove('open');
                  document.getElementById('previewBody').innerHTML = '';
                  lastPreviewData = null;
                  previewToken++;
                }

                function changePreview(delta) {
                  previewIndex = Math.min(Math.max(0, previewIndex + delta), selectedFiles.length - 1);
                  renderPreview();
                }

                function getNUpGrid(pagesPerSheet) {
                  switch (pagesPerSheet) {
                    case 2: return { cols: 2, rows: 1 };
                    case 4: return { cols: 2, rows: 2 };
                    case 6: return { cols: 3, rows: 2 };
                    case 9: return { cols: 3, rows: 3 };
                    case 16: return { cols: 4, rows: 4 };
                    default: return { cols: 1, rows: 1 };
                  }
                }

                function getPrintSettingsText() {
                  const copies = document.getElementById('printCopies').value || '1';
                  const color = document.getElementById('printColor').value === 'mono' ? 'Monochrome' : 'Color';
                  const sides = document.getElementById('printDuplex').value === 'duplex' ? 'Double-sided' : 'Single-sided';
                  const perSide = parseInt(document.getElementById('printPagesPerSheet').value, 10) || 1;
                  return `${copies} cop${copies === '1' ? 'y' : 'ies'} · ${color} · ${sides} · ${perSide} page${perSide === 1 ? '' : 's'} per side`;
                }

                function renderPreviewData(data) {
                  const body = document.getElementById('previewBody');
                  const note = `<div class="preview-note">${escapeHtml(getPrintSettingsText())}${data.message ? ' — ' + escapeHtml(data.message) : ''}</div>`;

                  if ((data.kind === 'pdf' || data.kind === 'image') && Array.isArray(data.pages) && data.pages.length > 0) {
                    const perSide = parseInt(document.getElementById('printPagesPerSheet').value, 10) || 1;
                    const grid = getNUpGrid(perSide);
                    const landscape = perSide > 1 && grid.cols > grid.rows;
                    let html = note;

                    for (let start = 0; start < data.pages.length; start += perSide) {
                      const sheetPages = data.pages.slice(start, start + perSide);
                      html += `<div class="preview-sheet${landscape ? ' landscape' : ''}"><div class="preview-sheet-grid" style="grid-template-columns:repeat(${grid.cols},1fr);grid-template-rows:repeat(${grid.rows},1fr);">`;

                      for (let i = 0; i < perSide; i++) {
                        html += sheetPages[i]
                          ? `<img src="${sheetPages[i]}" alt="Page ${start + i + 1}">`
                          : '<div></div>';
                      }

                      html += '</div></div>';
                    }

                    body.innerHTML = html;
                  } else if (data.kind === 'text' && typeof data.text === 'string') {
                    const pre = document.createElement('pre');
                    pre.textContent = data.text;
                    body.innerHTML = note;
                    body.appendChild(pre);
                  } else {
                    const message = data.message || 'Preview is not available for this file.';
                    body.innerHTML = `<div class="preview-note">${escapeHtml(getPrintSettingsText())}</div><div class="modal-empty">${escapeHtml(message)}</div>`;
                  }
                }

                async function renderPreview() {
                  const file = selectedFiles[previewIndex];
                  if (!file) return;

                  const body = document.getElementById('previewBody');
                  const token = ++previewToken;
                  lastPreviewData = null;

                  document.getElementById('previewTitle').innerText = file.name;
                  document.getElementById('previewCounter').innerText = selectedFiles.length > 1
                    ? `File ${previewIndex + 1} of ${selectedFiles.length}`
                    : '';
                  document.getElementById('btnPreviewPrev').disabled = previewIndex === 0;
                  document.getElementById('btnPreviewNext').disabled = previewIndex >= selectedFiles.length - 1;

                  body.innerHTML = '<div class="modal-empty">Loading preview…</div>';

                  const formData = new FormData();
                  formData.append('file', file, file.name);

                  try {
                    const res = await fetch('/api/preview', { method: 'POST', body: formData });
                    if (!res.ok) throw new Error('Preview request failed');

                    const data = await res.json();
                    if (token !== previewToken) return;

                    lastPreviewData = data;
                    renderPreviewData(data);
                  } catch (err) {
                    if (token !== previewToken) return;
                    body.innerHTML = '<div class="modal-empty">Unable to preview this file. It will still be printed normally.</div>';
                  }
                }

                function showAlert(msg, isSuccess = true) {
                  const alert = document.getElementById('statusAlert');
                  alert.className = 'alert ' + (isSuccess ? 'alert-success' : 'alert-error');
                  alert.innerText = msg;
                  alert.style.display = 'block';
                  window.scrollTo({ top: 0, behavior: 'smooth' });
                  setTimeout(() => { alert.style.display = 'none'; }, 9000);
                }

                function escapeHtml(value) {
                  return String(value ?? '').replace(/[&<>"']/g, ch => ({
                    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
                  }[ch]));
                }

                function updateScanCancelButton() {
                  const btn = document.getElementById('btnCancelScan');
                  if (!btn) return;
                  if (scanController) {
                    btn.innerHTML = ICON_X + ' Cancel Scan';
                    btn.style.display = 'inline-flex';
                  } else if (currentScannedBlob) {
                    btn.innerHTML = ICON_X + ' Clear Scan';
                    btn.style.display = 'inline-flex';
                  } else {
                    btn.style.display = 'none';
                  }
                }

                function clearScanResult(showMessage = true) {
                  currentScannedBlob = null;
                  currentScannedName = 'Scanned_Document.jpg';

                  const download = document.getElementById('btnDownloadScan');
                  if (download && download.href.startsWith('blob:')) URL.revokeObjectURL(download.href);
                  if (download) {
                    download.href = '#';
                    download.download = currentScannedName;
                  }

                  const img = document.getElementById('scanPreviewImg');
                  img.src = '';
                  img.style.display = 'none';
                  document.getElementById('scanPdfBox').style.display = 'none';
                  document.getElementById('scanPageCount').textContent = '';
                  document.getElementById('scanPreviewBox').style.display = 'none';

                  updateScanCancelButton();
                  if (showMessage) showAlert('Scanned document cleared.');
                }

                function cancelScan() {
                  if (scanController) {
                    scanController.abort();
                    return;
                  }
                  if (currentScannedBlob) clearScanResult();
                }

                function syncScanSource() {
                  const source = document.getElementById('scanSource');
                  const format = document.getElementById('scanFormat');
                  const hint = document.getElementById('adfScanHint');
                  const isAdf = source.value === 'adf';

                  hint.style.display = isAdf ? 'block' : 'none';
                  if (isAdf && format.value !== 'pdf') format.value = 'pdf';
                }

                document.getElementById('scanSource').addEventListener('change', syncScanSource);
                syncScanSource();

                async function triggerScan() {
                  const spinner = document.getElementById('scanSpinner');
                  const previewBox = document.getElementById('scanPreviewBox');
                  const pdfBox = document.getElementById('scanPdfBox');
                  const img = document.getElementById('scanPreviewImg');
                  const btn = document.getElementById('btnScan');

                  if (currentScannedBlob) clearScanResult(false);

                  scanController = new AbortController();
                  spinner.style.display = 'block';
                  previewBox.style.display = 'none';
                  btn.disabled = true;
                  updateScanCancelButton();

                  const format = document.getElementById('scanFormat').value;
                  const params = new URLSearchParams({
                    dpi: document.getElementById('scanDpi').value,
                    color: document.getElementById('scanColor').value,
                    source: document.getElementById('scanSource').value,
                    format: format,
                    device: document.getElementById('selectScanner').value
                  });

                  try {
                    const res = await fetch('/api/scan?' + params.toString(), { signal: scanController.signal });
                    if (!res.ok) {
                      const errData = await res.json();
                      throw new Error(errData.error || 'Scan failed');
                    }

                    const blob = await res.blob();
                    const contentType = res.headers.get('content-type') || 'image/jpeg';
                    const extension = format === 'pdf' ? 'pdf' : (format === 'png' ? 'png' : 'jpg');
                    const pageCount = Number(res.headers.get('x-scan-page-count') || '1');

                    currentScannedBlob = new File([blob], 'Scanned_Document.' + extension, { type: contentType });
                    currentScannedName = currentScannedBlob.name;

                    const url = URL.createObjectURL(blob);
                    document.getElementById('btnDownloadScan').href = url;
                    document.getElementById('btnDownloadScan').download = currentScannedName;
                    document.getElementById('scanPageCount').textContent = pageCount > 1 ? ' (' + pageCount + ' pages)' : '';

                    if (format === 'pdf') {
                      img.style.display = 'none';
                      img.src = '';
                      pdfBox.style.display = 'block';
                    } else {
                      img.style.display = 'block';
                      img.src = url;
                      pdfBox.style.display = 'none';
                    }

                    previewBox.style.display = 'block';
                    if (format !== 'pdf' && pageCount > 1) {
                      showAlert('Scanned ' + pageCount + ' pages, but only the first is shown. Choose PDF output to save every page.', false);
                    } else if (pageCount > 1) {
                      showAlert('Scanned ' + pageCount + ' pages into one PDF.');
                    } else {
                      showAlert('Document scanned successfully!');
                    }
                  } catch (err) {
                    if (err.name === 'AbortError') {
                      showAlert('Scan cancelled.', false);
                    } else {
                      showAlert('Scan failed: ' + err.message, false);
                    }
                  } finally {
                    scanController = null;
                    spinner.style.display = 'none';
                    updateScanCancelButton();
                    btn.disabled = false;
                  }
                }

                async function printScannedImage() {
                  if (!currentScannedBlob) return;
                  const printer = document.getElementById('selectPrinter').value;
                  if (!printer) {
                    showAlert('No shared printer is enabled.', false);
                    return;
                  }

                  const formData = new FormData();
                  formData.append('file', currentScannedBlob, currentScannedName);
                  formData.append('printer', printer);

                  try {
                    const res = await fetch('/api/print-file', { method: 'POST', body: formData });
                    const json = await res.json();
                    if (json.success) {
                      showAlert(json.message || 'Scan queued to printer!');
                      loadJobs();
                    } else {
                      showAlert('Print error: ' + (json.error || json.message), false);
                    }
                  } catch (err) {
                    showAlert('Error: ' + err.message, false);
                  }
                }

                function updatePrintCancelButton() {
                  const btn = document.getElementById('btnCancelPrint');
                  if (!btn) return;
                  if (printController) {
                    btn.innerHTML = ICON_X + ' Cancel Print';
                    btn.style.display = 'inline-flex';
                  } else if (selectedFiles.length > 0) {
                    btn.innerHTML = ICON_X + ' Clear Selection';
                    btn.style.display = 'inline-flex';
                  } else {
                    btn.style.display = 'none';
                  }
                }

                function clearSelectedFiles() {
                  selectedFiles = [];
                  document.getElementById('fileList').innerHTML = '';
                  document.getElementById('fileUploadName').innerText = 'Tap to select files';
                  document.getElementById('fileUploadInput').value = '';
                  document.getElementById('btnPrintFile').disabled = true;
                  document.getElementById('btnPreviewFile').disabled = true;
                  closePreview();
                  updatePrintCancelButton();
                  showAlert('Selected file(s) cleared.');
                }

                function handleFileSelected(input) {
                  selectedFiles = Array.from(input.files || []);
                  closePreview();
                  const list = document.getElementById('fileList');
                  const name = document.getElementById('fileUploadName');
                  const btn = document.getElementById('btnPrintFile');
                  const previewBtn = document.getElementById('btnPreviewFile');

                  if (selectedFiles.length === 0) {
                    name.innerText = 'Tap to select files';
                    list.innerHTML = '';
                    btn.disabled = true;
                    previewBtn.disabled = true;
                    closePreview();
                    updatePrintCancelButton();
                    return;
                  }

                  name.innerText = selectedFiles.length + ' file(s) selected';
                  list.innerHTML = selectedFiles.map((file, index) => `
                    <li>
                      <span>${escapeHtml(file.name)}</span>
                      <span class="muted">${Math.max(1, Math.round(file.size / 1024))} KB</span>
                    </li>
                  `).join('');
                  btn.disabled = false;
                  previewBtn.disabled = false;
                  updatePrintCancelButton();
                }

                function cancelPrint() {
                  if (printController) {
                    printController.abort();
                    return;
                  }
                  if (selectedFiles.length > 0) clearSelectedFiles();
                }

                async function submitPrintFile() {
                  if (selectedFiles.length === 0) return;
                  const printer = document.getElementById('selectPrinter').value;
                  if (!printer) {
                    showAlert('No shared printer is enabled.', false);
                    return;
                  }

                  const formData = new FormData();
                  selectedFiles.forEach(file => formData.append('file', file, file.name));
                  formData.append('printer', printer);
                  formData.append('copies', document.getElementById('printCopies').value || '1');
                  formData.append('color', document.getElementById('printColor').value);
                  formData.append('duplex', document.getElementById('printDuplex').value);
                  formData.append('pagesPerSheet', document.getElementById('printPagesPerSheet').value);

                  const btn = document.getElementById('btnPrintFile');
                  printController = new AbortController();
                  btn.disabled = true;
                  btn.innerText = 'Printing...';
                  updatePrintCancelButton();

                  try {
                    const res = await fetch('/api/print-file', { method: 'POST', body: formData, signal: printController.signal });
                    const json = await res.json();
                    if (json.success) {
                      showAlert(json.message || 'Document(s) sent to the printer.');
                      selectedFiles = [];
                      document.getElementById('fileList').innerHTML = '';
                      document.getElementById('fileUploadName').innerText = 'Tap to select files';
                      document.getElementById('fileUploadInput').value = '';
                      document.getElementById('btnPreviewFile').disabled = true;
                      closePreview();
                      loadJobs();
                    } else {
                      const detail = (json.jobs || []).filter(j => !j.success).map(j => j.message).join(' ');
                      showAlert('Failed: ' + (json.error || detail || json.message), false);
                      loadJobs();
                    }
                  } catch (err) {
                    if (err.name === 'AbortError') {
                      showAlert('Print request cancelled.', false);
                    } else {
                      showAlert('Print error: ' + err.message, false);
                    }
                  } finally {
                    printController = null;
                    updatePrintCancelButton();
                    btn.disabled = selectedFiles.length === 0;
                    btn.innerHTML = ICON_PRINTER + ' Print Document(s)';
                  }
                }

                async function loadJobs() {
                  try {
                    const res = await fetch('/api/jobs');
                    const jobs = await res.json();
                    const body = document.getElementById('jobsBody');

                    if (jobs.length === 0) {
                      body.innerHTML = '<tr><td colspan="8" style="text-align:center;" class="muted">No print jobs recorded yet.</td></tr>';
                      return;
                    }

                    body.innerHTML = jobs.map(j => {
                      const stateClass = j.StateCode === 9
                        ? 'badge-success'
                        : ((j.StateCode === 7 || j.StateCode === 8) ? 'badge-error' : 'badge-warn');

                      return `
                        <tr>
                          <td>#${j.JobId}</td>
                          <td><strong>${escapeHtml(j.DocumentName)}</strong></td>
                          <td>${escapeHtml(j.PrinterName)}</td>
                          <td>${escapeHtml(j.MimeType)}</td>
                          <td>${escapeHtml(j.Size)}</td>
                          <td><span class="badge ${stateClass}">${escapeHtml(j.State)}</span></td>
                          <td>${escapeHtml(j.Time)}</td>
                          <td>${escapeHtml(j.ErrorMessage || '—')}</td>
                        </tr>
                      `;
                    }).join('');
                  } catch {}
                }

                async function clearJobs() {
                  await fetch('/api/clear-jobs', { method: 'POST' });
                  loadJobs();
                }

                document.addEventListener('keydown', (e) => {
                  if (e.key === 'Escape') closePreview();
                  if (!document.getElementById('previewModal').classList.contains('open')) return;
                  if (e.key === 'ArrowLeft') changePreview(-1);
                  if (e.key === 'ArrowRight') changePreview(1);
                });

                ['printCopies', 'printColor', 'printDuplex', 'printPagesPerSheet'].forEach((id) => {
                  document.getElementById(id).addEventListener('change', () => {
                    if (lastPreviewData && document.getElementById('previewModal').classList.contains('open'))
                      renderPreviewData(lastPreviewData);
                  });
                });

                loadJobs();
                setInterval(loadJobs, 5000);
              </script>
            </body>
            </html>
            """;
    }
}
