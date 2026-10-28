using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PrintBridge.Printing;

public sealed class WindowsPrinterBridge
{
    public static IReadOnlyList<string> GetInstalledPrinters()
    {
        var list = new List<string>();
        try
        {
            foreach (string name in PrinterSettings.InstalledPrinters)
                list.Add(name);
        }
        catch
        {
        }

        return list;
    }

    public PrintResult SendToSpooler(
        string printerName,
        byte[] data,
        string documentName = "PrintBridge Job",
        string mimeType = "application/pdf",
        PrintOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(printerName) || data == null || data.Length == 0)
            return PrintResult.Fail("No printer or empty document was provided.");

        var extension = Path.GetExtension(documentName).ToLowerInvariant();
        var mime = (mimeType ?? string.Empty).ToLowerInvariant();

        if (string.IsNullOrEmpty(extension))
            extension = ExtensionFromMime(mime);

        try
        {
            if (IsPdf(data) || mime.Contains("pdf"))
            {
                return PrintPdfNative(printerName, data, documentName, options)
                    ? PrintResult.Ok()
                    : PrintResult.Fail("The PDF could not be rendered. Make sure the printer is online and its driver is working.");
            }

            if (IsUrf(data) || mime.Contains("urf"))
            {
                return PrintUrfNative(printerName, data, documentName, options)
                    ? PrintResult.Ok()
                    : PrintResult.Fail("The AirPrint raster document could not be decoded. Try printing the document as PDF instead.");
            }

            if (mime.Contains("pwg") || mime.Contains("raster"))
            {
                return PrintResult.Fail("PWG raster documents are not supported directly. Send the job as PDF or an image instead.");
            }

            if (IsImage(data, extension, mime))
            {
                return PrintImageNative(printerName, data, documentName, options)
                    ? PrintResult.Ok()
                    : PrintResult.Fail("The image could not be printed. The format may not be supported by the printer driver.");
            }

            var text = ExtractPrintableText(data, extension, mime);
            if (text != null)
            {
                if (string.IsNullOrWhiteSpace(text))
                    return PrintResult.Fail("The document did not contain any printable text.");

                return PrintTextNative(printerName, text, documentName, options)
                    ? PrintResult.Ok()
                    : PrintResult.Fail("The text document could not be printed.");
            }

            if (IsRawPrinterLanguage(mime, extension))
            {
                return SendRawBytesToSpooler(printerName, data, documentName)
                    ? PrintResult.Ok()
                    : PrintResult.Fail("The printer rejected the raw print data. Check that the printer driver understands PCL/PostScript.");
            }

            if (DocumentTextExtractor.LooksLikeText(data))
            {
                return PrintTextNative(printerName, DocumentTextExtractor.DecodeText(data), documentName, options)
                    ? PrintResult.Ok()
                    : PrintResult.Fail("The text document could not be printed.");
            }

            return PrintResult.Fail(
                $"Unsupported document format '{mimeType}'{(!string.IsNullOrEmpty(extension) ? $" ({extension})" : "")}. " +
                "Supported formats: PDF, JPG, PNG, BMP, GIF, TIFF, TXT, CSV, HTML, RTF, DOCX, XLSX, PPTX, ODT, ODS and ODP.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Printing error for {printerName}: {ex}");
            return PrintResult.Fail($"Printing failed: {ex.Message}");
        }
    }

    public bool PrintTestPage(string printerName)
    {
        try
        {
            using var doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = printerName;

            if (!doc.PrinterSettings.IsValid)
                return false;

            doc.DocumentName = "Easy Print Easy Scan Test Page";
            doc.PrintController = new StandardPrintController();

            doc.PrintPage += (s, e) =>
            {
                if (e.Graphics == null) return;

                using var titleFont = new Font("Segoe UI", 20, FontStyle.Bold);
                using var bodyFont = new Font("Segoe UI", 11, FontStyle.Regular);
                using var monoFont = new Font("Consolas", 10, FontStyle.Regular);
                using var brush = new SolidBrush(Color.FromArgb(30, 30, 30));
                using var accentBrush = new SolidBrush(Color.FromArgb(95, 107, 69));

                int x = 60, y = 60;

                e.Graphics.DrawString("Easy Print Easy Scan Test Page", titleFont, accentBrush, x, y);
                y += 50;

                e.Graphics.DrawString("Success! This printer is correctly shared over Wi-Fi.", bodyFont, brush, x, y);
                y += 35;

                var details = $"""
                    Queue Name:      {printerName}
                    Date & Time:     {DateTime.Now:yyyy-MM-dd HH:mm:ss}
                    Computer:        {Environment.MachineName}
                    Protocols:       AirPrint (iOS / iPadOS) & Mopria / IPP Everywhere (Android)
                    Engine:          Windows GDI Direct Rendering
                    Status:          Active and Ready
                    """;

                e.Graphics.DrawString(details, monoFont, brush, x, y);
            };

            doc.Print();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to print test page: {ex.Message}");
            return false;
        }
    }

    private static bool PrintPdfNative(string printerName, byte[] data, string documentName, PrintOptions? options)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"pb_{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(tempFile, data);

            return Task.Run(async () =>
            {
                var storageFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(tempFile));
                var pdfDoc = await PdfDocument.LoadFromFileAsync(storageFile);
                uint pageCount = pdfDoc.PageCount;
                if (pageCount == 0) return false;

                using var printDoc = new PrintDocument();
                printDoc.PrinterSettings.PrinterName = printerName;
                if (!printDoc.PrinterSettings.IsValid) return false;

                ApplyPrintOptions(printDoc, options);
                printDoc.DocumentName = documentName;
                printDoc.PrintController = new StandardPrintController();

                var pagesPerSheet = NormalizePagesPerSheet(options?.PagesPerSheet ?? 1);
                var (columns, rows) = ConfigureNUp(printDoc, pagesPerSheet);
                uint currentPage = 0;

                printDoc.PrintPage += (s, e) =>
                {
                    if (e.Graphics == null)
                    {
                        e.Cancel = true;
                        return;
                    }

                    for (int cell = 0; cell < pagesPerSheet && currentPage < pageCount; cell++, currentPage++)
                    {
                        using var page = pdfDoc.GetPage(currentPage);
                        using var stream = new InMemoryRandomAccessStream();

                        page.RenderToStreamAsync(stream).AsTask().GetAwaiter().GetResult();
                        stream.Seek(0);

                        using var netStream = stream.AsStreamForRead();
                        using var img = Image.FromStream(netStream);

                        DrawImageInCell(e, img, cell, columns, rows);
                    }

                    e.HasMorePages = currentPage < pageCount;
                };

                printDoc.Print();
                return true;
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Native Windows.Data.Pdf print failed: {ex.Message}");
            return false;
        }
        finally
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(300_000);
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            });
        }
    }

    private static bool PrintImageNative(string printerName, byte[] data, string documentName, PrintOptions? options)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);

            using var printDoc = new PrintDocument();
            printDoc.PrinterSettings.PrinterName = printerName;
            if (!printDoc.PrinterSettings.IsValid) return false;

            ApplyPrintOptions(printDoc, options);
            printDoc.DocumentName = documentName;
            printDoc.PrintController = new StandardPrintController();

            FrameDimension? dimension = null;
            int frameCount = 1;

            if (img.FrameDimensionsList.Length > 0)
            {
                dimension = new FrameDimension(img.FrameDimensionsList[0]);
                try { frameCount = Math.Max(1, img.GetFrameCount(dimension)); }
                catch { frameCount = 1; }
            }

            var pagesPerSheet = NormalizePagesPerSheet(options?.PagesPerSheet ?? 1);
            var (columns, rows) = ConfigureNUp(printDoc, pagesPerSheet);
            int frameIndex = 0;

            printDoc.PrintPage += (s, e) =>
            {
                if (e.Graphics == null)
                {
                    e.Cancel = true;
                    return;
                }

                for (int cell = 0; cell < pagesPerSheet && frameIndex < frameCount; cell++, frameIndex++)
                {
                    if (dimension != null)
                    {
                        try { img.SelectActiveFrame(dimension, frameIndex); } catch { }
                    }

                    DrawImageInCell(e, img, cell, columns, rows);
                }

                e.HasMorePages = frameIndex < frameCount;
            };

            printDoc.Print();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Image print failed: {ex.Message}");
            return false;
        }
    }

    private static bool PrintUrfNative(string printerName, byte[] data, string documentName, PrintOptions? options)
    {
        List<Bitmap>? pages = null;
        try
        {
            pages = DecodeUrfPages(data);
            if (pages.Count == 0) return false;

            using var printDoc = new PrintDocument();
            printDoc.PrinterSettings.PrinterName = printerName;
            if (!printDoc.PrinterSettings.IsValid) return false;

            ApplyPrintOptions(printDoc, options);
            printDoc.DocumentName = documentName;
            printDoc.PrintController = new StandardPrintController();

            var pagesPerSheet = NormalizePagesPerSheet(options?.PagesPerSheet ?? 1);
            var (columns, rows) = ConfigureNUp(printDoc, pagesPerSheet);
            int pageIndex = 0;
            printDoc.PrintPage += (s, e) =>
            {
                if (e.Graphics == null)
                {
                    e.Cancel = true;
                    return;
                }

                for (int cell = 0; cell < pagesPerSheet && pageIndex < pages.Count; cell++, pageIndex++)
                {
                    DrawImageInCell(e, pages[pageIndex], cell, columns, rows);
                }

                e.HasMorePages = pageIndex < pages.Count;
            };

            printDoc.Print();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"URF print failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (pages != null)
            {
                foreach (var page in pages)
                    page.Dispose();
            }
        }
    }

    private static bool PrintTextNative(string printerName, string text, string documentName, PrintOptions? options)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            using var printDoc = new PrintDocument();
            printDoc.PrinterSettings.PrinterName = printerName;
            if (!printDoc.PrinterSettings.IsValid) return false;

            ApplyPrintOptions(printDoc, options);
            printDoc.DocumentName = documentName;
            printDoc.PrintController = new StandardPrintController();

            using var font = new Font("Segoe UI", 10.5f);
            using var brush = new SolidBrush(Color.Black);

            var lines = text.Replace("\0", string.Empty)
                .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var pagesPerSheet = NormalizePagesPerSheet(options?.PagesPerSheet ?? 1);
            var (columns, rows) = ConfigureNUp(printDoc, pagesPerSheet);
            int linesPerPage = 0;
            int lineIndex = 0;

            printDoc.PrintPage += (s, e) =>
            {
                if (e.Graphics == null)
                {
                    e.Cancel = true;
                    return;
                }

                var bounds = e.MarginBounds;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    e.HasMorePages = false;
                    return;
                }

                float lineHeight = font.GetHeight(e.Graphics) + 2;

                if (pagesPerSheet == 1)
                {
                    float y = bounds.Top;

                    while (lineIndex < lines.Length && y + lineHeight <= bounds.Bottom)
                    {
                        e.Graphics.DrawString(lines[lineIndex], font, brush, bounds.Left, y);
                        y += lineHeight;
                        lineIndex++;
                    }
                }
                else
                {
                    float logicalWidth = Math.Min(bounds.Width, bounds.Height);
                    float logicalHeight = Math.Max(bounds.Width, bounds.Height);

                    if (linesPerPage <= 0)
                        linesPerPage = Math.Max(1, (int)Math.Floor(logicalHeight / lineHeight));

                    for (int cell = 0; cell < pagesPerSheet && lineIndex < lines.Length; cell++)
                    {
                        var cellBounds = GetCellBounds(bounds, cell, columns, rows);
                        float scale = Math.Min(cellBounds.Width / logicalWidth, cellBounds.Height / logicalHeight);
                        if (scale <= 0) continue;

                        float offsetX = cellBounds.Left + (cellBounds.Width - logicalWidth * scale) / 2;
                        float offsetY = cellBounds.Top + (cellBounds.Height - logicalHeight * scale) / 2;

                        var state = e.Graphics.Save();
                        try
                        {
                            e.Graphics.TranslateTransform(offsetX, offsetY);
                            e.Graphics.ScaleTransform(scale, scale);

                            float y = 0;
                            for (int i = 0; i < linesPerPage && lineIndex < lines.Length; i++)
                            {
                                e.Graphics.DrawString(lines[lineIndex], font, brush, 0, y);
                                y += lineHeight;
                                lineIndex++;
                            }
                        }
                        finally
                        {
                            e.Graphics.Restore(state);
                        }
                    }
                }

                e.HasMorePages = lineIndex < lines.Length;
            };

            printDoc.Print();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Text print failed: {ex.Message}");
            return false;
        }
    }

    private static int NormalizePagesPerSheet(int value) =>
        value is 2 or 4 or 6 or 9 or 16 ? value : 1;

    private static (int Columns, int Rows) GetNUpGrid(int pagesPerSheet) => pagesPerSheet switch
    {
        2 => (2, 1),
        4 => (2, 2),
        6 => (3, 2),
        9 => (3, 3),
        16 => (4, 4),
        _ => (1, 1)
    };

    private static (int Columns, int Rows) ConfigureNUp(PrintDocument printDoc, int pagesPerSheet)
    {
        var grid = GetNUpGrid(pagesPerSheet);
        if (pagesPerSheet > 1 && grid.Columns > grid.Rows)
            printDoc.DefaultPageSettings.Landscape = true;
        return grid;
    }

    private static RectangleF GetCellBounds(RectangleF bounds, int cellIndex, int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);

        float cellWidth = bounds.Width / columns;
        float cellHeight = bounds.Height / rows;
        int column = Math.Max(0, cellIndex % columns);
        int row = Math.Max(0, cellIndex / columns);

        return new RectangleF(
            bounds.Left + column * cellWidth,
            bounds.Top + row * cellHeight,
            cellWidth,
            cellHeight);
    }

    private static void DrawImageInCell(PrintPageEventArgs e, Image image, int cellIndex, int columns, int rows)
    {
        var bounds = e.MarginBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var cell = GetCellBounds(bounds, cellIndex, columns, rows);
        float padding = Math.Min(cell.Width, cell.Height) * 0.04f;
        if (padding > 1 && cell.Width > padding * 2 && cell.Height > padding * 2)
        {
            cell = new RectangleF(
                cell.Left + padding,
                cell.Top + padding,
                cell.Width - padding * 2,
                cell.Height - padding * 2);
        }

        float ratioX = cell.Width / image.Width;
        float ratioY = cell.Height / image.Height;
        float ratio = Math.Min(ratioX, ratioY);

        int width = Math.Max(1, (int)(image.Width * ratio));
        int height = Math.Max(1, (int)(image.Height * ratio));
        int x = (int)(cell.Left + (cell.Width - width) / 2);
        int y = (int)(cell.Top + (cell.Height - height) / 2);

        e.Graphics?.DrawImage(image, x, y, width, height);
    }

    private static void ApplyPrintOptions(PrintDocument printDoc, PrintOptions? options)
    {
        if (options == null) return;

        try
        {
            if (options.Copies > 1)
                printDoc.PrinterSettings.Copies = (short)Math.Clamp(options.Copies, 1, 99);
        }
        catch { }

        try
        {
            if (options.Duplex && printDoc.PrinterSettings.CanDuplex)
                printDoc.PrinterSettings.Duplex = Duplex.Vertical;
            else if (!options.Duplex)
                printDoc.PrinterSettings.Duplex = Duplex.Simplex;
        }
        catch { }

        try
        {
            if (options.Color.HasValue)
                printDoc.DefaultPageSettings.Color = options.Color.Value;
        }
        catch { }
    }

    private static string? ExtractPrintableText(byte[] data, string extension, string mime)
    {
        if (extension is ".html" or ".htm" || mime.Contains("html"))
            return DocumentTextExtractor.TextFromHtml(DocumentTextExtractor.DecodeText(data));

        if (extension == ".rtf" || mime.Contains("rtf"))
            return DocumentTextExtractor.TextFromRtf(DocumentTextExtractor.DecodeText(data));

        if (DocumentTextExtractor.IsOfficeExtension(extension))
            return DocumentTextExtractor.ExtractOfficeText(data, extension);

        if (DocumentTextExtractor.IsTextFormat(extension, mime))
            return DocumentTextExtractor.DecodeText(data);

        return null;
    }

    private static string ExtensionFromMime(string mime) => mime switch
    {
        "application/pdf" => ".pdf",
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/bmp" => ".bmp",
        "image/gif" => ".gif",
        "image/tiff" => ".tif",
        "text/plain" => ".txt",
        "text/csv" => ".csv",
        "text/markdown" => ".md",
        "text/html" => ".html",
        "application/rtf" => ".rtf",
        "application/json" => ".json",
        "application/xml" or "text/xml" => ".xml",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
        "application/vnd.oasis.opendocument.text" => ".odt",
        "application/vnd.oasis.opendocument.spreadsheet" => ".ods",
        "application/vnd.oasis.opendocument.presentation" => ".odp",
        _ => string.Empty
    };

    private static bool IsPdf(byte[] data) =>
        data.Length >= 4 && data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F';

    private static bool IsImage(byte[] data, string extension, string mime)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return true;

        if (data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return true;

        if (data.Length >= 2 && data[0] == 0x42 && data[1] == 0x4D)
            return true;

        if (data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8')
            return true;

        if (data.Length >= 4
            && ((data[0] == 'I' && data[1] == 'I' && data[2] == 0x2A && data[3] == 0x00)
                || (data[0] == 'M' && data[1] == 'M' && data[2] == 0x00 && data[3] == 0x2A)))
            return true;

        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !mime.Contains("urf", StringComparison.OrdinalIgnoreCase)
            && !mime.Contains("pwg", StringComparison.OrdinalIgnoreCase)
            && !mime.Contains("svg", StringComparison.OrdinalIgnoreCase))
            return true;

        return extension is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff"
            or ".ico" or ".webp" or ".wmf" or ".emf";
    }

    private static bool IsUrf(byte[] data) =>
        data.Length >= 8 && data[0] == 'U' && data[1] == 'N' && data[2] == 'I' && data[3] == 'R'
        && data[4] == 'A' && data[5] == 'S' && data[6] == 'T';

    private static bool IsRawPrinterLanguage(string mime, string extension) =>
        mime.Contains("postscript", StringComparison.OrdinalIgnoreCase)
        || mime.Contains("pcl", StringComparison.OrdinalIgnoreCase)
        || extension is ".ps" or ".pcl" or ".prn";

    private static List<Bitmap> DecodeUrfPages(byte[] data)
    {
        var list = new List<Bitmap>();
        if (!IsUrf(data) || data.Length < 12) return list;

        int pageCount = ReadInt32BigEndian(data, 8);
        int pos = 12;
        int pageIndex = 0;

        while (pos + 32 <= data.Length
               && (pageCount <= 0 || pageIndex < pageCount)
               && pageIndex < 500)
        {
            int bpp = data[pos];
            int width = ReadInt32BigEndian(data, pos + 12);
            int height = ReadInt32BigEndian(data, pos + 16);
            pos += 32;

            if (bpp <= 0 || width <= 0 || height <= 0 || width > 20000 || height > 30000)
                break;

            if ((long)width * height > 80_000_000L)
                break;

            int bytesPerPixel = bpp switch
            {
                1 => 1,
                8 => 1,
                24 => 3,
                32 => 4,
                _ => Math.Max(1, (bpp + 7) / 8)
            };

            int bytesPerRow = bpp == 1 ? (width + 7) / 8 : width * bytesPerPixel;
            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var rect = new Rectangle(0, 0, width, height);
            var bitmapData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, bitmap.PixelFormat);

            try
            {
                var line = new byte[bytesPerRow];
                var outputRow = new byte[width * 3];
                int y = 0;

                while (y < height)
                {
                    if (pos >= data.Length)
                        break;

                    // Each raster line starts with a repeat count (0 = once).
                    int lineRepeat = data[pos++] + 1;
                    int rowPos = 0;
                    bool truncated = false;

                    while (rowPos < bytesPerRow)
                    {
                        if (pos >= data.Length)
                        {
                            truncated = true;
                            break;
                        }

                        int code = data[pos++];

                        if (code < 0x80)
                        {
                            // Repeat the next pixel (code + 1) times.
                            int repeat = code + 1;
                            if (pos + bytesPerPixel > data.Length)
                            {
                                truncated = true;
                                break;
                            }

                            for (int i = 0; i < repeat && rowPos < bytesPerRow; i++)
                            {
                                int copy = Math.Min(bytesPerPixel, bytesPerRow - rowPos);
                                Array.Copy(data, pos, line, rowPos, copy);
                                rowPos += copy;
                            }

                            pos += bytesPerPixel;
                        }
                        else if (code == 0x80)
                        {
                            // Fill the rest of the line with the next pixel.
                            if (pos + bytesPerPixel > data.Length)
                            {
                                truncated = true;
                                break;
                            }

                            while (rowPos < bytesPerRow)
                            {
                                int copy = Math.Min(bytesPerPixel, bytesPerRow - rowPos);
                                Array.Copy(data, pos, line, rowPos, copy);
                                rowPos += copy;
                            }

                            pos += bytesPerPixel;
                        }
                        else
                        {
                            // Copy (257 - code) pixels verbatim.
                            int pixelCount = 257 - code;
                            int bytesToCopy = Math.Min(pixelCount * bytesPerPixel, bytesPerRow - rowPos);
                            int available = Math.Min(bytesToCopy, data.Length - pos);

                            Array.Copy(data, pos, line, rowPos, available);
                            pos += available;
                            rowPos += available;

                            if (available < bytesToCopy)
                            {
                                truncated = true;
                                break;
                            }
                        }
                    }

                    if (truncated)
                        break;

                    int repetitions = Math.Min(lineRepeat, height - y);
                    for (int r = 0; r < repetitions; r++)
                    {
                        ConvertUrfLine(line, outputRow, width, bpp, bytesPerPixel);
                        IntPtr destination = IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride);
                        Marshal.Copy(outputRow, 0, destination, outputRow.Length);
                        y++;
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            list.Add(bitmap);
            pageIndex++;
        }

        return list;
    }

    private static void ConvertUrfLine(byte[] line, byte[] outputRow, int width, int bpp, int bytesPerPixel)
    {
        if (bpp == 1)
        {
            for (int x = 0; x < width; x++)
            {
                bool black = (line[x >> 3] & (0x80 >> (x & 7))) != 0;
                byte value = black ? (byte)0 : (byte)255;
                int offset = x * 3;
                outputRow[offset] = value;
                outputRow[offset + 1] = value;
                outputRow[offset + 2] = value;
            }
        }
        else if (bytesPerPixel >= 3)
        {
            for (int x = 0; x < width; x++)
            {
                int source = x * bytesPerPixel;
                int offset = x * 3;
                outputRow[offset] = line[source];
                outputRow[offset + 1] = line[source + 1];
                outputRow[offset + 2] = line[source + 2];
            }
        }
        else
        {
            for (int x = 0; x < width; x++)
            {
                byte value = line[x];
                int offset = x * 3;
                outputRow[offset] = value;
                outputRow[offset + 1] = value;
                outputRow[offset + 2] = value;
            }
        }
    }

    private static int ReadInt32BigEndian(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    private static bool SendRawBytesToSpooler(string printerName, byte[] data, string docName)
    {
        if (!NativeMethods.OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
            return false;

        try
        {
            var di = new NativeMethods.DOCINFOA
            {
                pDocName = docName,
                pDataType = "RAW"
            };

            if (!NativeMethods.StartDocPrinter(hPrinter, 1, di))
                return false;

            try
            {
                if (!NativeMethods.StartPagePrinter(hPrinter))
                    return false;

                try
                {
                    var pUnmanaged = Marshal.AllocCoTaskMem(data.Length);
                    try
                    {
                        Marshal.Copy(data, 0, pUnmanaged, data.Length);
                        return NativeMethods.WritePrinter(hPrinter, pUnmanaged, data.Length, out _);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pUnmanaged);
                    }
                }
                finally
                {
                    NativeMethods.EndPagePrinter(hPrinter);
                }
            }
            finally
            {
                NativeMethods.EndDocPrinter(hPrinter);
            }
        }
        finally
        {
            NativeMethods.ClosePrinter(hPrinter);
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public class DOCINFOA
        {
            [MarshalAs(UnmanagedType.LPStr)] public string? pDocName;
            [MarshalAs(UnmanagedType.LPStr)] public string? pOutputFile;
            [MarshalAs(UnmanagedType.LPStr)] public string? pDataType;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterA", SetLastError = true,
            CharSet = CharSet.Ansi, ExactSpelling = true)]
        public static extern bool OpenPrinter(string szPrinter, out IntPtr hPrinter, IntPtr pd);

        [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true, ExactSpelling = true)]
        public static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterA", SetLastError = true,
            CharSet = CharSet.Ansi, ExactSpelling = true)]
        public static extern bool StartDocPrinter(IntPtr hPrinter, int level,
            [In, MarshalAs(UnmanagedType.LPStruct)] DOCINFOA di);

        [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true, ExactSpelling = true)]
        public static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true, ExactSpelling = true)]
        public static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true, ExactSpelling = true)]
        public static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true, ExactSpelling = true)]
        public static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);
    }
}
