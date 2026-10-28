using Microsoft.Win32;
using PrintBridge.Models;
using PrintBridge.Network;
using PrintBridge.Printing;
using PrintBridge.Server;
using PrintBridge.Settings;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PrintBridge.UI;

/// <summary>
/// Settings and configuration window for PrintBridge.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly SettingsManager _settings;
    private readonly PrintBridgeServer _server;
    private readonly WindowsPrinterBridge _printerBridge;
    private readonly DataGridView _grid;
    private readonly NumericUpDown _portInput;
    private readonly CheckBox _autoStartCheck;
    private readonly Label _statusLabel;
    private readonly Label _addressLabel;

    public SettingsForm(SettingsManager settings, PrintBridgeServer server)
    {
        _settings = settings;
        _server = server;
        _printerBridge = new WindowsPrinterBridge();

        Text = "Easy Print Easy Scan — Wi-Fi Printer Sharing";
        Size = new Size(740, 580);
        MinimumSize = new Size(680, 500);
        StartPosition = FormStartPosition.CenterScreen;
        UiTheme.StyleForm(this);
        Icon = TrayApplicationContext.CreateDefaultIcon();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 5,
            ColumnCount = 1,
            Padding = new Padding(20),
            BackColor = UiTheme.Background
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // Header card
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // Grid
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // Quick Options
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // Firewall & Tools
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // Action Buttons

        // ──────────────── 1. Header Card ────────────────
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 88,
            Padding = new Padding(0, 0, 0, 10),
            BackColor = Color.Transparent
        };

        var titleIcon = new PictureBox
        {
            Image = UiTheme.CreateIcon(AppIcons.Printer, UiTheme.SageStrong, 26),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(26, 26),
            BackColor = Color.Transparent,
            Location = new Point(0, 4)
        };
        headerPanel.Controls.Add(titleIcon);

        var titleLabel = new Label
        {
            Text = "Easy Print Easy Scan",
            Font = UiTheme.HeadingFont,
            ForeColor = UiTheme.Text,
            AutoSize = true,
            Location = new Point(36, -3)
        };
        headerPanel.Controls.Add(titleLabel);

        _statusLabel = new Label
        {
            Text = "Active on Wi-Fi (AirPrint & IPP Everywhere ready)",
            Font = new Font(UiTheme.BodyFont, FontStyle.Bold),
            ForeColor = UiTheme.Success,
            AutoSize = true,
            Location = new Point(0, 36)
        };
        headerPanel.Controls.Add(_statusLabel);

        _addressLabel = new Label
        {
            Text = $"Network Address: http://{NetworkHelper.GetLocalIPv4Address()}:{_settings.Current.IppPort} (Host: {NetworkHelper.GetHostName()})",
            ForeColor = UiTheme.Muted,
            AutoSize = true,
            Location = new Point(0, 60)
        };
        headerPanel.Controls.Add(_addressLabel);

        root.Controls.Add(headerPanel, 0, 0);

        // ──────────────── 2. Printer Grid ────────────────
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            BackgroundColor = UiTheme.Surface,
            BorderStyle = BorderStyle.None,
            RowTemplate = { Height = 36 }
        };

        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Enabled",
            HeaderText = "Share",
            Width = 60,
            FillWeight = 12
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "WindowsName",
            HeaderText = "Windows Printer",
            ReadOnly = true,
            FillWeight = 34
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "DisplayName",
            HeaderText = "AirPrint Name (Shown on Phone)",
            FillWeight = 32
        });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Color",
            HeaderText = "Color",
            Width = 55,
            FillWeight = 11
        });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Duplex",
            HeaderText = "Duplex",
            Width = 60,
            FillWeight = 11
        });

        UiTheme.StyleGrid(_grid);
        LoadPrintersToGrid();
        root.Controls.Add(_grid, 0, 1);

        // ──────────────── 3. Options Panel ────────────────
        var optionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 10, 0, 8),
            BackColor = Color.Transparent
        };

        optionsPanel.Controls.Add(new Label
        {
            Text = "IPP Port:",
            AutoSize = true,
            Padding = new Padding(0, 4, 4, 0)
        });

        _portInput = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Value = _settings.Current.IppPort,
            Width = 80
        };
        optionsPanel.Controls.Add(_portInput);

        optionsPanel.Controls.Add(new Label { Width = 25 }); // Spacer

        _autoStartCheck = new CheckBox
        {
            Text = "Start automatically with Windows",
            Checked = _settings.Current.AutoStart,
            AutoSize = true,
            Padding = new Padding(0, 2, 0, 0)
        };
        optionsPanel.Controls.Add(_autoStartCheck);

        root.Controls.Add(optionsPanel, 0, 2);

        // ──────────────── 4. Tools & Actions Bar ────────────────
        var toolsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 4, 0, 14),
            BackColor = Color.Transparent
        };

        var fwBtn = new Button
        {
            Text = "Open Firewall Ports (1-Click)",
            AutoSize = true,
            Padding = new Padding(10, 6, 12, 6),
            Cursor = Cursors.Hand
        };
        UiTheme.StyleSecondaryButton(fwBtn);
        UiTheme.SetButtonIcon(fwBtn, AppIcons.Lock);
        fwBtn.Click += (s, e) => OpenFirewallPorts();
        toolsPanel.Controls.Add(fwBtn);

        var refreshBtn = new Button
        {
            Text = "Scan for New Printers",
            AutoSize = true,
            Padding = new Padding(10, 6, 12, 6),
            Cursor = Cursors.Hand
        };
        UiTheme.StyleSecondaryButton(refreshBtn);
        UiTheme.SetButtonIcon(refreshBtn, AppIcons.Scan);
        refreshBtn.Click += (s, e) => ScanForPrinters();
        toolsPanel.Controls.Add(refreshBtn);

        var testBtn = new Button
        {
            Text = "Print Test Page",
            AutoSize = true,
            Padding = new Padding(10, 6, 12, 6),
            Cursor = Cursors.Hand
        };
        UiTheme.StyleSecondaryButton(testBtn);
        UiTheme.SetButtonIcon(testBtn, AppIcons.Printer);
        testBtn.Click += (s, e) => PrintTestPageSelected();
        toolsPanel.Controls.Add(testBtn);

        var jobsBtn = new Button
        {
            Text = "Print Jobs",
            AutoSize = true,
            Padding = new Padding(10, 6, 12, 6),
            Cursor = Cursors.Hand
        };
        UiTheme.StyleSecondaryButton(jobsBtn);
        UiTheme.SetButtonIcon(jobsBtn, AppIcons.List);
        jobsBtn.Click += (s, e) =>
        {
            using var jobsForm = new PrintJobsForm(_settings, _server);
            jobsForm.ShowDialog(this);
        };
        toolsPanel.Controls.Add(jobsBtn);

        root.Controls.Add(toolsPanel, 0, 3);

        // ──────────────── 5. Footer Buttons ────────────────
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Color.Transparent
        };

        var saveBtn = new Button
        {
            Text = "Save & Apply",
            Width = 118,
            Height = 36
        };
        UiTheme.StylePrimaryButton(saveBtn);
        saveBtn.Click += (s, e) => SaveSettingsAndClose();

        var cancelBtn = new Button
        {
            Text = "Close",
            Width = 88,
            Height = 36
        };
        UiTheme.StyleSecondaryButton(cancelBtn);
        cancelBtn.Click += (s, e) => Close();

        footer.Controls.Add(saveBtn);
        footer.Controls.Add(cancelBtn);

        root.Controls.Add(footer, 0, 4);

        Controls.Add(root);
    }

    private void LoadPrintersToGrid()
    {
        _grid.Rows.Clear();
        foreach (var p in _settings.Current.Printers)
        {
            _grid.Rows.Add(p.Enabled, p.WindowsPrinterName, p.DisplayName, p.Color, p.Duplex);
        }
    }

    private void ScanForPrinters()
    {
        var installed = WindowsPrinterBridge.GetInstalledPrinters();
        var existing = _settings.Current.Printers.Select(p => p.WindowsPrinterName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        int added = 0;
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
                added++;
            }
        }

        if (added > 0)
        {
            LoadPrintersToGrid();
            MessageBox.Show($"Discovered {added} new printer(s)!", "Easy Print Easy Scan",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show("No new printers detected. All installed printers are already listed.",
                "Easy Print Easy Scan", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void PrintTestPageSelected()
    {
        if (_grid.CurrentRow == null)
        {
            MessageBox.Show("Please select a printer row first.", "Easy Print Easy Scan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var queueName = _grid.CurrentRow.Cells["WindowsName"].Value?.ToString();
        if (string.IsNullOrEmpty(queueName)) return;

        var success = _printerBridge.PrintTestPage(queueName);
        if (success)
        {
            MessageBox.Show($"Test page successfully queued for '{queueName}'!", "Easy Print Easy Scan",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show($"Failed to queue test page for '{queueName}'. Make sure the printer is turned on and ready.",
                "Easy Print Easy Scan", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenFirewallPorts()
    {
        var ok = FirewallHelper.AddFirewallRules((int)_portInput.Value);
        if (ok)
        {
            MessageBox.Show(
                "Windows Firewall rules have been added for TCP port 631 (IPP) and UDP port 5353 (mDNS / AirPrint)!",
                "Firewall Configured", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void SaveSettingsAndClose()
    {
        var previousLocations = _settings.Current.Printers
            .GroupBy(p => p.WindowsPrinterName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Location, StringComparer.OrdinalIgnoreCase);

        _settings.Current.Printers.Clear();
        for (int i = 0; i < _grid.Rows.Count; i++)
        {
            var row = _grid.Rows[i];
            var windowsName = row.Cells["WindowsName"].Value?.ToString() ?? "";
            _settings.Current.Printers.Add(new PrinterConfig
            {
                Enabled = (bool)(row.Cells["Enabled"].Value ?? true),
                WindowsPrinterName = windowsName,
                DisplayName = row.Cells["DisplayName"].Value?.ToString() ?? "",
                Color = (bool)(row.Cells["Color"].Value ?? true),
                Duplex = (bool)(row.Cells["Duplex"].Value ?? false),
                Location = previousLocations.TryGetValue(windowsName, out var location) ? location : ""
            });
        }

        var oldPort = _settings.Current.IppPort;
        var newPort = (int)_portInput.Value;
        _settings.Current.IppPort = newPort;
        _settings.Current.AutoStart = _autoStartCheck.Checked;
        _settings.Save();

        // Update Windows startup registry key
        UpdateAutoStartRegistry(_autoStartCheck.Checked);

        // Refresh mDNS advertisement
        _server.RefreshAdvertisement();

        if (oldPort != newPort)
        {
            MessageBox.Show(
                "Port changed! Please restart Easy Print Easy Scan for the new port binding to take effect.",
                "Easy Print Easy Scan", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        Close();
    }

    private static void UpdateAutoStartRegistry(bool enable)
    {
        try
        {
            const string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            using var key = Registry.CurrentUser.OpenSubKey(runKey, true);
            if (enable)
            {
                var exePath = Application.ExecutablePath;
                key?.SetValue("PrintBridge", $"\"{exePath}\"");
            }
            else
            {
                key?.DeleteValue("PrintBridge", false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to update registry: {ex.Message}");
        }
    }
}
