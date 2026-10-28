using System.Drawing;
using System.Drawing.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PrintBridge.Printing;

public sealed class DocumentPreviewResult
{
    public string Kind { get; set; } = "unsupported";
    public string FileName { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public List<string> Pages { get; } = new();
    public string? Text { get; set; }
    public string? Message { get; set; }
}

public static class DocumentPreview
{
    private const int MaxPages = 12;
    private const int MaxTextChars = 200_000;
    private const uint PageRenderWidth = 1000;

    public static async Task<DocumentPreviewResult> CreateAsync(byte[] data, string fileName, string mimeType)
    {
        var result = new DocumentPreviewResult { FileName = fileName };

        if (data.Length == 0)
        {
            result.Message = "The file is empty.";
            return result;
        }

        if (IsPdf(data, mimeType))
            return await RenderPdfAsync(data, result);

        if (TryGetImage(data, mimeType))
            return RenderImage(data, result);

        var text = ExtractText(data, fileName, mimeType);
        if (text != null)
        {
            result.Kind = "text";
            result.Text = text.Length > MaxTextChars
                ? text[..MaxTextChars] + "\n\n… (preview truncated)"
                : text;
            return result;
        }

        result.Message = "Preview is not available for this file type. It will still be printed normally.";
        return result;
    }

    private static bool IsPdf(byte[] data, string mimeType) =>
        (data.Length >= 4
         && data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46)
        || mimeType.Contains("pdf", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetImage(byte[] data, string mimeType)
    {
        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (data.Length < 4)
            return false;

        if (data[0] == 0xFF && data[1] == 0xD8) return true;
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return true;
        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return true;
        if (data[0] == 0x42 && data[1] == 0x4D) return true;
        if (data[0] == 0x49 && data[1] == 0x49 && data[2] == 0x2A && data[3] == 0x00) return true;
        if (data[0] == 0x4D && data[1] == 0x4D && data[2] == 0x00 && data[3] == 0x2A) return true;
        return false;
    }

    private static DocumentPreviewResult RenderImage(byte[] data, DocumentPreviewResult result)
    {
        result.Kind = "image";

        try
        {
            using var ms = new MemoryStream(data);
            using var image = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);

            FrameDimension? dimension = null;
            int frameCount = 1;

            if (image.FrameDimensionsList.Length > 0)
            {
                dimension = new FrameDimension(image.FrameDimensionsList[0]);
                try { frameCount = Math.Max(1, image.GetFrameCount(dimension)); }
                catch { frameCount = 1; }
            }

            result.PageCount = frameCount;
            int renderCount = Math.Min(frameCount, MaxPages);

            for (int i = 0; i < renderCount; i++)
            {
                if (dimension != null)
                {
                    try { image.SelectActiveFrame(dimension, i); } catch { }
                }

                using var preview = CreatePreviewBitmap(image, 1400, 1800);
                using var output = new MemoryStream();
                preview.Save(output, ImageFormat.Png);
                result.Pages.Add("data:image/png;base64," + Convert.ToBase64String(output.ToArray()));
            }

            if (frameCount > renderCount)
                result.Message = $"Showing the first {renderCount} of {frameCount} pages.";
        }
        catch
        {
            result.Kind = "unsupported";
            result.Message = "This image could not be decoded for preview.";
        }

        return result;
    }

    private static async Task<DocumentPreviewResult> RenderPdfAsync(byte[] data, DocumentPreviewResult result)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"pb_preview_{Guid.NewGuid():N}.pdf");

        try
        {
            await File.WriteAllBytesAsync(tempFile, data);

            var storageFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(tempFile));
            var pdf = await PdfDocument.LoadFromFileAsync(storageFile);
            var pageCount = (int)pdf.PageCount;

            result.Kind = "pdf";
            result.PageCount = pageCount;

            if (pageCount == 0)
            {
                result.Message = "The PDF does not contain any pages.";
                return result;
            }

            int renderCount = Math.Min(pageCount, MaxPages);
            var options = new PdfPageRenderOptions { DestinationWidth = PageRenderWidth };

            for (int i = 0; i < renderCount; i++)
            {
                using var page = pdf.GetPage((uint)i);
                using var stream = new InMemoryRandomAccessStream();

                await page.RenderToStreamAsync(stream, options);
                stream.Seek(0);

                using var netStream = stream.AsStreamForRead();
                using var image = Image.FromStream(netStream);

                using var output = new MemoryStream();
                image.Save(output, ImageFormat.Png);
                result.Pages.Add("data:image/png;base64," + Convert.ToBase64String(output.ToArray()));
            }

            if (pageCount > renderCount)
                result.Message = $"Showing the first {renderCount} of {pageCount} pages.";
        }
        catch
        {
            result.Kind = "unsupported";
            result.Message = "This PDF could not be rendered for preview.";
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }

        return result;
    }

    private static Bitmap CreatePreviewBitmap(Image source, int maxWidth, int maxHeight)
    {
        float ratio = Math.Min(
            1f,
            Math.Min((float)maxWidth / source.Width, (float)maxHeight / source.Height));

        int width = Math.Max(1, (int)(source.Width * ratio));
        int height = Math.Max(1, (int)(source.Height * ratio));

        var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, width, height);
        return bitmap;
    }

    private static string? ExtractText(byte[] data, string fileName, string mimeType)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (extension is ".html" or ".htm" || mimeType.Contains("html", StringComparison.OrdinalIgnoreCase))
            return DocumentTextExtractor.TextFromHtml(DocumentTextExtractor.DecodeText(data));

        if (extension == ".rtf" || mimeType.Contains("rtf", StringComparison.OrdinalIgnoreCase))
            return DocumentTextExtractor.TextFromRtf(DocumentTextExtractor.DecodeText(data));

        if (DocumentTextExtractor.IsOfficeExtension(extension))
            return DocumentTextExtractor.ExtractOfficeText(data, extension);

        if (DocumentTextExtractor.IsTextFormat(extension, mimeType))
            return DocumentTextExtractor.DecodeText(data);

        if (DocumentTextExtractor.LooksLikeText(data))
            return DocumentTextExtractor.DecodeText(data);

        return null;
    }
}
