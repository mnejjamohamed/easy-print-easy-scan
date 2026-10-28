using PrintBridge.Models;
using PrintBridge.Network;
using PrintBridge.Server;
using PrintBridge.Settings;
using System.Diagnostics;

namespace PrintBridge.UI;

public sealed class PrintJobsForm : Form
{
    private readonly PrintBridgeServer _server;
    private readonly DataGridView _grid;
    private readonly Label _summaryLabel;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private string _lastSignature = string.Empty;

    public PrintJobsForm(SettingsManager settings, PrintBridgeServer server)
    {
        _server = server;

        Text = "Easy Print Easy Scan — Print Jobs";
        Size = new Size(980, 560);
        MinimumSize = new Size(760, 420);
        StartPosition = FormStartPosition.CenterScreen;
        UiTheme.StyleForm(this);
        Icon = TrayApplicationContext.CreateDefaultIcon();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16),
            BackColor = UiTheme.Background
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 66,
            BackColor = Color.Transparent
        };

        var titleIcon = new PictureBox
        {
            Image = UiTheme.CreateIcon(AppIcons.List, UiTheme.SageStrong, 26),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(26, 26),
            BackColor = Color.Transparent,
            Location = new Point(0, 3)
        };
        header.Controls.Add(titleIcon);

        var title = new Label
        {
            Text = "Print Jobs",
            Font = UiTheme.SubheadingFont,
            ForeColor = UiTheme.Text,
            AutoSize = true,
            Location = new Point(36, -2)
        };
        header.Controls.Add(title);

        var subtitle = new Label
        {
            Text = $"Live queue for {NetworkHelper.GetLocalIPv4Address()}:{_server.BoundPort}",
            ForeColor = UiTheme.Muted,
            AutoSize = true,
            Location = new Point(38, 38)
        };
        header.Controls.Add(subtitle);

        _summaryLabel = new Label
        {
            Text = "Loading...",
            ForeColor = UiTheme.Muted,
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(600, 14)
        };
        header.Controls.Add(_summaryLabel);
        header.Resize += (s, e) =>
        {
            _summaryLabel.Left = Math.Max(0, header.ClientSize.Width - _summaryLabel.Width);
        };

        root.Controls.Add(header, 0, 0);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = UiTheme.Surface,
            BorderStyle = BorderStyle.None,
            MultiSelect = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowTemplate = { Height = 32 }
        };

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "JobId",
            HeaderText = "#",
            FillWeight = 6
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Document",
            HeaderText = "Document",
            FillWeight = 26
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Printer",
            HeaderText = "Printer",
            FillWeight = 18
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Format",
            HeaderText = "Format",
            FillWeight = 14
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Size",
            HeaderText = "Size",
            FillWeight = 8
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Status",
            HeaderText = "Status",
            FillWeight = 10
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Time",
            HeaderText = "Time",
            FillWeight = 10
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Details",
            HeaderText = "Details",
            FillWeight = 24
        });

        _grid.CellFormatting += (s, e) =>
        {
            if (e.ColumnIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Status")
                return;

            var status = e.Value?.ToString();
            if (e.CellStyle == null) return;

            e.CellStyle.ForeColor = status switch
            {
                "Completed" => UiTheme.Success,
                "Failed" => UiTheme.Danger,
                _ => UiTheme.Warning
            };
        };

        _grid.CellDoubleClick += (s, e) =>
        {
            if (e.RowIndex < 0) return;
            var details = _grid.Rows[e.RowIndex].Cells["Details"].Value?.ToString();
            if (!string.IsNullOrWhiteSpace(details) && details != "—")
                MessageBox.Show(details, "Job Details", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        UiTheme.StyleGrid(_grid);
        root.Controls.Add(_grid, 0, 1);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 12, 0, 0),
            BackColor = Color.Transparent
        };

        var closeBtn = new Button
        {
            Text = "Close",
            Width = 88,
            Height = 34
        };
        UiTheme.StyleSecondaryButton(closeBtn);
        closeBtn.Click += (s, e) => Close();

        var webBtn = new Button
        {
            Text = "Web Dashboard",
            AutoSize = true,
            Padding = new Padding(10, 6, 12, 6),
            Height = 34
        };
        UiTheme.StyleSecondaryButton(webBtn);
        UiTheme.SetButtonIcon(webBtn, AppIcons.Globe);
        webBtn.Click += (s, e) => OpenWebDashboard();

        var clearBtn = new Button
        {
            Text = "Clear History",
            AutoSize = true,
            Padding = new Padding(10, 6, 12, 6),
            Height = 34
        };
        UiTheme.StyleDangerButton(clearBtn);
        UiTheme.SetButtonIcon(clearBtn, AppIcons.Delete);
        clearBtn.Click += (s, e) => ClearHistory();

        var reprintBtn = new Button
        {
            Text = "Reprint Selected",
            AutoSize = true,
            Padding = new Padding(10, 6, 12, 6),
            Height = 34
        };
        UiTheme.StylePrimaryButton(reprintBtn);
        UiTheme.SetButtonIcon(reprintBtn, AppIcons.Printer);
        reprintBtn.Click += (s, e) => ReprintSelected();

        var refreshBtn = new Button
        {
            Text = "Refresh",
            AutoSize = true,
            Padding = new Padding(10, 6, 12, 6),
            Height = 34
        };
        UiTheme.StyleSecondaryButton(refreshBtn);
        UiTheme.SetButtonIcon(refreshBtn, AppIcons.Refresh);
        refreshBtn.Click += (s, e) => LoadJobs(force: true);

        footer.Controls.Add(closeBtn);
        footer.Controls.Add(webBtn);
        footer.Controls.Add(clearBtn);
        footer.Controls.Add(reprintBtn);
        footer.Controls.Add(refreshBtn);

        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);

        _server.JobReceived += OnJobReceived;

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        _refreshTimer.Tick += (s, e) => LoadJobs();
        _refreshTimer.Start();

        FormClosed += (s, e) =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _server.JobReceived -= OnJobReceived;
        };

        LoadJobs(force: true);
    }

    private void OnJobReceived(PrintJob job)
    {
        if (IsDisposed || !IsHandleCreated) return;

        try
        {
            BeginInvoke(new Action(() => LoadJobs(force: true)));
        }
        catch
        {
        }
    }

    private void LoadJobs(bool force = false)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => LoadJobs(force)));
            return;
        }

        var jobs = _server.GetAllJobs();
        var signature = string.Join("|", jobs.Select(j =>
            $"{j.JobId}:{(int)j.State}:{j.ErrorMessage}:{j.DocumentName}"));

        var completed = jobs.Count(j => j.State == PrintJobState.Completed);
        var failed = jobs.Count(j => j.State == PrintJobState.Aborted);
        var active = jobs.Count(j => j.State is PrintJobState.Pending or PrintJobState.Processing);

        _summaryLabel.Text = $"{jobs.Count} total  •  {completed} completed  •  {active} active  •  {failed} failed";
        _summaryLabel.Left = Math.Max(0, _summaryLabel.Parent!.ClientSize.Width - _summaryLabel.Width);

        if (!force && signature == _lastSignature)
            return;

        _lastSignature = signature;

        int selectedJobId = 0;
        if (_grid.CurrentRow != null)
        {
            int.TryParse(_grid.CurrentRow.Cells["JobId"].Value?.ToString(), out selectedJobId);
        }

        _grid.Rows.Clear();

        foreach (var job in jobs)
        {
            var index = _grid.Rows.Add(
                job.JobId,
                job.DocumentName,
                job.PrinterName,
                job.MimeType,
                job.FriendlySize,
                job.FriendlyState,
                job.ReceivedAt.ToLocalTime().ToString("HH:mm:ss"),
                string.IsNullOrWhiteSpace(job.ErrorMessage) ? "—" : job.ErrorMessage);

            if (job.JobId == selectedJobId)
            {
                _grid.ClearSelection();
                _grid.Rows[index].Selected = true;
                _grid.CurrentCell = _grid.Rows[index].Cells[0];
            }
        }
    }

    private void ReprintSelected()
    {
        if (_grid.CurrentRow == null)
        {
            MessageBox.Show("Select a print job first.", "Easy Print Easy Scan",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!int.TryParse(_grid.CurrentRow.Cells["JobId"].Value?.ToString(), out var jobId))
            return;

        var job = _server.GetAllJobs().FirstOrDefault(j => j.JobId == jobId);
        if (job == null || job.DocumentData.Length == 0)
        {
            MessageBox.Show("This job has no stored document data to reprint.", "Easy Print Easy Scan",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var result = _server.SubmitDirectJob(job.PrinterName, job.DocumentName, job.MimeType, job.DocumentData);

        if (result.State == PrintJobState.Completed)
        {
            MessageBox.Show($"Job #{result.JobId} was sent to '{result.PrinterName}' again.", "Easy Print Easy Scan",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(result.ErrorMessage ?? "Reprint failed.", "Easy Print Easy Scan",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        LoadJobs(force: true);
    }

    private void ClearHistory()
    {
        var answer = MessageBox.Show(
            "Clear the print job history? This does not remove jobs from the Windows spooler.",
            "Easy Print Easy Scan", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (answer != DialogResult.Yes) return;

        _server.ClearJobs();
        _lastSignature = string.Empty;
        LoadJobs(force: true);
    }

    private void OpenWebDashboard()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"http://{NetworkHelper.GetLocalIPv4Address()}:{_server.BoundPort}",
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }
}
