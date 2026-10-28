using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;

namespace PrintBridge.Scanning;

public static class ScanPdfBuilder
{
    public static byte[]? CreatePdf(IReadOnlyList<byte[]> images, int dpi)
    {
        if (images.Count == 0) return null;
        if (dpi <= 0) dpi = 300;

        var pages = new List<(byte[] Jpeg, int Width, int Height)>();
        foreach (var bytes in images)
        {
            var page = ConvertToJpeg(bytes);
            if (page.HasValue) pages.Add(page.Value);
        }

        if (pages.Count == 0) return null;

        int objectCount = 2 + pages.Count * 3;
        var offsets = new long[objectCount + 1];

        using var output = new MemoryStream();

        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));

        void BeginObject(int number)
        {
            offsets[number] = output.Position;
            Write($"{number} 0 obj\n");
        }

        void EndObject() => Write("endobj\n");

        Write("%PDF-1.4\n");
        output.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A });

        BeginObject(1);
        Write("<< /Type /Catalog /Pages 2 0 R >>\n");
        EndObject();

        var kids = new StringBuilder();
        for (int i = 0; i < pages.Count; i++)
        {
            if (i > 0) kids.Append(' ');
            kids.Append($"{3 + i * 3} 0 R");
        }

        BeginObject(2);
        Write($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\n");
        EndObject();

        for (int i = 0; i < pages.Count; i++)
        {
            int pageObject = 3 + i * 3;
            int imageObject = 4 + i * 3;
            int contentObject = 5 + i * 3;
            var page = pages[i];

            double widthPoints = page.Width * 72.0 / dpi;
            double heightPoints = page.Height * 72.0 / dpi;
            string widthText = widthPoints.ToString("0.##", CultureInfo.InvariantCulture);
            string heightText = heightPoints.ToString("0.##", CultureInfo.InvariantCulture);

            BeginObject(pageObject);
            Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {widthText} {heightText}] " +
                  $"/Resources << /XObject << /Im0 {imageObject} 0 R >> >> /Contents {contentObject} 0 R >>\n");
            EndObject();

            BeginObject(imageObject);
            Write($"<< /Type /XObject /Subtype /Image /Width {page.Width} /Height {page.Height} " +
                  $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {page.Jpeg.Length} >>\nstream\n");
            output.Write(page.Jpeg);
            Write("\nendstream\n");
            EndObject();

            var content = Encoding.ASCII.GetBytes(
                $"q\n{widthText} 0 0 {heightText} 0 0 cm\n/Im0 Do\nQ\n");

            BeginObject(contentObject);
            Write($"<< /Length {content.Length} >>\nstream\n");
            output.Write(content);
            Write("\nendstream\n");
            EndObject();
        }

        long xrefPosition = output.Position;
        Write($"xref\n0 {objectCount + 1}\n");
        Write("0000000000 65535 f \n");
        for (int i = 1; i <= objectCount; i++)
            Write($"{offsets[i]:D10} 00000 n \n");

        Write($"trailer\n<< /Size {objectCount + 1} /Root 1 0 R >>\n");
        Write($"startxref\n{xrefPosition}\n%%EOF\n");

        return output.ToArray();
    }

    private static (byte[] Jpeg, int Width, int Height)? ConvertToJpeg(byte[] bytes)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            using var image = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: false);

            using var rgb = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(rgb))
            {
                graphics.Clear(Color.White);
                graphics.DrawImage(image, 0, 0, image.Width, image.Height);
            }

            using var jpeg = new MemoryStream();
            rgb.Save(jpeg, ImageFormat.Jpeg);
            return (jpeg.ToArray(), rgb.Width, rgb.Height);
        }
        catch
        {
            return null;
        }
    }
}
