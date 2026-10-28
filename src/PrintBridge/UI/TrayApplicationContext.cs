using PrintBridge.Models;
using PrintBridge.Network;
using PrintBridge.Printing;
using PrintBridge.Server;
using PrintBridge.Settings;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PrintBridge.UI;

/// <summary>
/// Root application context managing the Windows System Tray icon,
/// background server lifecycle, and user interactions.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly SettingsManager _settings;
    private readonly PrintBridgeServer _server;
    private SettingsForm? _settingsForm;
    private PrintJobsForm? _jobsForm;

    public TrayApplicationContext()
    {
        _settings = new SettingsManager();
        _server = new PrintBridgeServer(_settings);

        _contextMenu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new SageColorTable()),
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.Text,
            Font = UiTheme.BodyFont
        };
        BuildContextMenu();

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateDefaultIcon(),
            ContextMenuStrip = _contextMenu,
            Visible = true,
            Text = "Easy Print Easy Scan — AirPrint & Wi-Fi Sharing"
        };

        _notifyIcon.DoubleClick += (s, e) => ShowSettings();

        // Notify user on print jobs
        _server.JobReceived += OnJobReceived;

        // Start background engine
        _ = StartServerAsync();
    }

    private async Task StartServerAsync()
    {
        await _server.StartAsync();

        var ip = NetworkHelper.GetLocalIPv4Address();
        var port = _server.BoundPort;

        if (!string.IsNullOrWhiteSpace(_server.LastError))
        {
            _notifyIcon.BalloonTipTitle = "Easy Print Easy Scan could not start";
            _notifyIcon.BalloonTipText = _server.LastError;
            _notifyIcon.BalloonTipIcon = ToolTipIcon.Error;
        }
        else
        {
            _notifyIcon.BalloonTipTitle = "Easy Print Easy Scan is Running";
            _notifyIcon.BalloonTipText = $"Printers are shared on http://{ip}:{port}\nReady for iPhone, iPad, and Android!";
            _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        }

        _notifyIcon.ShowBalloonTip(5000);

        BuildContextMenu();
    }

    private void BuildContextMenu()
    {
        _contextMenu.Items.Clear();

        var port = _server.BoundPort;
        var ip = NetworkHelper.GetLocalIPv4Address();

        // 1. Network Header
        var header = new ToolStripMenuItem(_server.IsRunning
            ? $"{ip}:{port} (Active)"
            : $"{ip}:{port} (Not running)")
        {
            Enabled = false,
            Font = new Font(_contextMenu.Font, FontStyle.Bold),
            Image = UiTheme.CreateIcon(
                _server.IsRunning ? AppIcons.Wifi : AppIcons.Warning,
                _server.IsRunning ? UiTheme.Success : UiTheme.Warning)
        };
        _contextMenu.Items.Add(header);
        _contextMenu.Items.Add(new ToolStripSeparator());

        // 2. Individual Printer Toggles
        var printers = _settings.Current.Printers;
        if (printers.Count == 0)
        {
            _contextMenu.Items.Add(new ToolStripMenuItem("No printers found") { Enabled = false });
        }
        else
        {
            foreach (var p in printers)
            {
                var name = string.IsNullOrWhiteSpace(p.DisplayName) ? p.WindowsPrinterName : p.DisplayName;
                var item = new ToolStripMenuItem(name)
                {
                    Checked = p.Enabled,
                    CheckOnClick = true,
                    Tag = p
                };
                item.CheckedChanged += OnPrinterToggled;
                _contextMenu.Items.Add(item);
            }
        }

        _contextMenu.Items.Add(new ToolStripSeparator());

        // 3. Settings & Actions
        AddMenuItem("Settings...", AppIcons.Settings, ShowSettings);
        AddMenuItem("Print Jobs...", AppIcons.List, ShowPrintJobs);
        AddMenuItem("View Web Dashboard", AppIcons.Globe, OpenWebDashboard);
        AddMenuItem("Configure Firewall", AppIcons.Lock, () => FirewallHelper.AddFirewallRules(port));
        AddMenuItem("Refresh Printers", AppIcons.Refresh, RefreshPrinters);

        _contextMenu.Items.Add(new ToolStripSeparator());

        // 4. Exit
        AddMenuItem("Exit", AppIcons.Cancel, ExitApplication, UiTheme.Danger);
    }

    private void AddMenuItem(string text, string glyph, Action action, Color? color = null)
    {
        var item = new ToolStripMenuItem(text)
        {
            Image = UiTheme.CreateIcon(glyph, color ?? UiTheme.Text)
        };
        item.Click += (s, e) => action();
        _contextMenu.Items.Add(item);
    }

    private void OnPrinterToggled(object? sender, EventArgs e)
    {
        if (sender is ToolStripMenuItem item && item.Tag is PrinterConfig cfg)
        {
            cfg.Enabled = item.Checked;
            _settings.Save();
            _server.RefreshAdvertisement();
        }
    }

    private void RefreshPrinters()
    {
        var installed = WindowsPrinterBridge.GetInstalledPrinters();
        var existing = _settings.Current.Printers.Select(p => p.WindowsPrinterName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in installed)
        {
            if (!existing.Contains(name))
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
        }
        _settings.Save();
        _server.RefreshAdvertisement();
        BuildContextMenu();
    }

    private void OpenWebDashboard()
    {
        var ip = NetworkHelper.GetLocalIPv4Address();
        var port = _server.BoundPort;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"http://{ip}:{port}",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void ShowSettings()
    {
        if (_settingsForm != null && !_settingsForm.IsDisposed)
        {
            _settingsForm.BringToFront();
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_settings, _server);
        _settingsForm.FormClosed += (s, e) =>
        {
            BuildContextMenu();
        };
        _settingsForm.Show();
    }

    private void ShowPrintJobs()
    {
        if (_jobsForm != null && !_jobsForm.IsDisposed)
        {
            _jobsForm.BringToFront();
            _jobsForm.Activate();
            return;
        }

        _jobsForm = new PrintJobsForm(_settings, _server);
        _jobsForm.FormClosed += (s, e) => _jobsForm = null;
        _jobsForm.Show();
    }

    private void OnJobReceived(PrintJob job)
    {
        _notifyIcon.BalloonTipTitle = "Print Job Received";
        _notifyIcon.BalloonTipText = $"Printing '{job.DocumentName}' to {job.PrinterName}...";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(3000);
    }

    private void ExitApplication()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _server.Dispose();
        Application.Exit();
    }

    /// <summary>
    /// Generates a sharp, native printer icon programmatically so no external .ico asset is required.
    /// </summary>
    public static Icon CreateDefaultIcon()
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // Outer Printer Body
            using var bodyBrush = new SolidBrush(UiTheme.SageStrong);
            g.FillRoundedRectangle(bodyBrush, new RectangleF(3, 9, 26, 15), 3);

            // Paper Feed
            using var paperBrush = new SolidBrush(Color.White);
            g.FillRectangle(paperBrush, 8, 3, 16, 9);
            using var paperPen = new Pen(UiTheme.Border, 1);
            g.DrawRectangle(paperPen, 8, 3, 16, 9);

            // Output Slot
            using var slotBrush = new SolidBrush(UiTheme.Text);
            g.FillRectangle(slotBrush, 7, 18, 18, 2);

            // Output Paper
            g.FillRectangle(paperBrush, 9, 19, 14, 9);
            g.DrawRectangle(paperPen, 9, 19, 14, 9);

            // Status LED
            using var ledBrush = new SolidBrush(UiTheme.Sage);
            g.FillEllipse(ledBrush, 22, 12, 4, 4);
        }

        var handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, RectangleF rect, float radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        float diameter = radius * 2;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
