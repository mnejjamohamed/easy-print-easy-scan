using System.Security.Cryptography;
using System.Text;

namespace PrintBridge.Models;

public static class PrinterIdentity
{
    public static string GetUuid(string seed)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("PrintBridge:" + seed));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash).ToString();
    }

    public static string GetQueuePath(PrinterConfig printer) =>
        printer.WindowsPrinterName.Replace(' ', '_');
}
