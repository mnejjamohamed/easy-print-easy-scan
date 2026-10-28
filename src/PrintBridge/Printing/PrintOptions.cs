namespace PrintBridge.Printing;

public sealed class PrintOptions
{
    public int Copies { get; set; } = 1;
    public bool Duplex { get; set; }
    public bool? Color { get; set; }
    public int? Quality { get; set; }
    public int PagesPerSheet { get; set; } = 1;
}

public readonly record struct PrintResult(bool Success, string? Error)
{
    public static PrintResult Ok() => new(true, null);
    public static PrintResult Fail(string error) => new(false, error);
}
