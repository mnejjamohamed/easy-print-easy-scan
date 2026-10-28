using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PrintBridge.UI;

/// <summary>
/// Shared Anthropic-inspired palette, icon helpers, and control styling.
/// </summary>
internal static class UiTheme
{
    public static readonly Color Background = Color.FromArgb(250, 249, 245);
    public static readonly Color Surface = Color.FromArgb(255, 255, 255);
    public static readonly Color Text = Color.FromArgb(31, 30, 28);
    public static readonly Color Muted = Color.FromArgb(107, 106, 99);
    public static readonly Color Border = Color.FromArgb(230, 227, 216);
    public static readonly Color Sage = Color.FromArgb(204, 213, 174);
    public static readonly Color SageSoft = Color.FromArgb(233, 237, 201);
    public static readonly Color SageStrong = Color.FromArgb(95, 107, 69);
    public static readonly Color OnSage = Color.FromArgb(49, 58, 36);
    public static readonly Color Danger = Color.FromArgb(163, 58, 47);
    public static readonly Color DangerSoft = Color.FromArgb(246, 229, 224);
    public static readonly Color Warning = Color.FromArgb(138, 106, 47);
    public static readonly Color Success = Color.FromArgb(77, 107, 63);

    public static readonly Font BodyFont = new("Segoe UI", 9.5f);
    public static readonly Font HeadingFont = new("Georgia", 18f, FontStyle.Bold);
    public static readonly Font SubheadingFont = new("Georgia", 17f, FontStyle.Bold);

    private static readonly string IconFontName = ResolveIconFontName();

    private static string ResolveIconFontName()
    {
        using var fonts = new InstalledFontCollection();
        var names = fonts.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Contains("Segoe Fluent Icons")) return "Segoe Fluent Icons";
        if (names.Contains("Segoe MDL2 Assets")) return "Segoe MDL2 Assets";
        return "Segoe UI Symbol";
    }

    public static Image CreateIcon(string glyph, Color color, int size = 16)
    {
        var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var font = new Font(IconFontName, size * 0.82f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoClip
            };
            g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);
        }
        return bitmap;
    }

    public static void SetButtonIcon(Button button, string glyph)
    {
        var previous = button.Image;
        button.Image = CreateIcon(glyph, button.ForeColor, 16);
        button.ImageAlign = ContentAlignment.MiddleLeft;
        button.TextImageRelation = TextImageRelation.ImageBeforeText;
        previous?.Dispose();
    }

    public static void StylePrimaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Sage;
        button.ForeColor = OnSage;
        button.Font = new Font(BodyFont, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
    }

    public static void StyleSecondaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Border;
        button.BackColor = Surface;
        button.ForeColor = Text;
        button.Font = BodyFont;
        button.Cursor = Cursors.Hand;
    }

    public static void StyleDangerButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = DangerSoft;
        button.ForeColor = Danger;
        button.Font = BodyFont;
        button.Cursor = Cursors.Hand;
    }

    public static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.GridColor = Border;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersHeight = 36;
        grid.ColumnHeadersDefaultCellStyle.BackColor = SageSoft;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Text;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = SageSoft;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Text;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font(BodyFont, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = Text;
        grid.DefaultCellStyle.SelectionBackColor = Sage;
        grid.DefaultCellStyle.SelectionForeColor = OnSage;
        grid.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Background;
        grid.AlternatingRowsDefaultCellStyle.ForeColor = Text;
        grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Sage;
        grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = OnSage;
    }

    public static void StyleForm(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
        form.Font = BodyFont;
    }
}

/// <summary>
/// Glyph codes for the Windows system icon fonts.
/// </summary>
internal static class AppIcons
{
    public const string Printer = "\uE749";
    public const string Scan = "\uE8FE";
    public const string Settings = "\uE713";
    public const string List = "\uE8FD";
    public const string Globe = "\uE774";
    public const string Lock = "\uE72E";
    public const string Refresh = "\uE72C";
    public const string Delete = "\uE74D";
    public const string Cancel = "\uE711";
    public const string Download = "\uE896";
    public const string Upload = "\uE898";
    public const string View = "\uE890";
    public const string Document = "\uE8A5";
    public const string Warning = "\uE7BA";
    public const string Wifi = "\uE701";
}

internal sealed class SageColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => UiTheme.Surface;
    public override Color ImageMarginGradientBegin => UiTheme.Surface;
    public override Color ImageMarginGradientMiddle => UiTheme.Surface;
    public override Color ImageMarginGradientEnd => UiTheme.Surface;
    public override Color MenuItemSelected => UiTheme.SageSoft;
    public override Color MenuItemBorder => UiTheme.Sage;
    public override Color MenuBorder => UiTheme.Border;
    public override Color SeparatorDark => UiTheme.Border;
    public override Color SeparatorLight => UiTheme.Border;
}
