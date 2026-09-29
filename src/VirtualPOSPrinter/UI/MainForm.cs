using System.Diagnostics;
using VirtualPOSPrinter.EscPos;
using VirtualPOSPrinter.Models;
using VirtualPOSPrinter.Services;

namespace VirtualPOSPrinter.UI;

public sealed class MainForm : Form
{
    private readonly PrinterManager _manager = new();
    private readonly ListView _printersView = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill };
    private readonly ListView _jobsView = new() { View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, Dock = DockStyle.Fill };
    private readonly PreviewPanel _preview = new();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private TabPage _tabPreview = null!;
    private readonly ListBox _log = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), HorizontalScrollbar = true, SelectionMode = SelectionMode.MultiExtended };
    private readonly Label _jobsHeader = new() { Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
    private readonly ToolStripButton _btnEdit = new("Edit") { Enabled = false };
    private readonly ToolStripButton _btnRemove = new("Remove") { Enabled = false };
    private SplitContainer _mainSplit = null!;
    private SplitContainer _rightSplit = null!;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly NotifyIcon _tray = new();
    private readonly bool _startHidden;
    private bool _exitRequested;
    private bool _initStarted;
    private bool _closing;

    public MainForm(bool startMinimized)
    {
        _startHidden = startMinimized || _settings.StartMinimized;
        Text = "VirtualPOSPrinter";
        ClientSize = new Size(1260, 760);
        MinimumSize = new Size(800, 500);
        StartPosition = FormStartPosition.CenterScreen;

        Icon = AppIcon.Create();
        BuildLayout();
        BuildTray();

        AppLog.Logged += OnLogged;
        _manager.PrinterChanged += p => { if (!_closing) RefreshPrinter(p); };
        _manager.JobReceived += OnJobReceived;

        Load += (_, _) =>
        {
            // Set after layout so the sizes are not distorted by DPI scaling.
            _mainSplit.SplitterDistance = Math.Min(500, Math.Max(_mainSplit.Panel1MinSize, _mainSplit.Width * 2 / 5));
            _rightSplit.SplitterDistance = Math.Min(150, Math.Max(_rightSplit.Panel1MinSize, _rightSplit.Height / 4));
        };
        FormClosing += OnFormClosing;
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };
    }

    /// <summary>When starting minimized the window is never shown: it only gets a handle so listeners can start.</summary>
    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && !IsHandleCreated)
        {
            CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_initStarted) return;
        _initStarted = true;
        BeginInvoke(async () => await InitializeAsync());
    }

    private void BuildLayout()
    {
        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        var btnAdd = new ToolStripButton("Add printer");
        btnAdd.Click += async (_, _) => await AddPrinterAsync();
        _btnEdit.Click += async (_, _) => await EditSelectedAsync();
        _btnRemove.Click += async (_, _) => await RemoveSelectedAsync();
        var btnFolder = new ToolStripButton("Open jobs folder");
        btnFolder.Click += (_, _) => OpenJobsFolder();
        var admin = NetworkService.IsElevated();
        var lblAdmin = new ToolStripLabel(admin ? "Administrator" : "Not elevated (secondary IPs unavailable)")
        {
            Alignment = ToolStripItemAlignment.Right,
            ForeColor = admin ? Color.SeaGreen : Color.DarkOrange
        };
        tools.Items.AddRange(new ToolStripItem[] { btnAdd, _btnEdit, _btnRemove, new ToolStripSeparator(), btnFolder, BuildSettingsMenu(), lblAdmin });

        _printersView.Columns.Add("Name", 110);
        _printersView.Columns.Add("Label", 60);
        _printersView.Columns.Add("Address", 120);
        _printersView.Columns.Add("Status", 215);
        _printersView.SelectedIndexChanged += (_, _) => OnPrinterSelected();
        _printersView.DoubleClick += async (_, _) => await EditSelectedAsync();
        BuildPrinterMenu();

        _jobsView.Columns.Add("Received", 160);
        _jobsView.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _jobsView.Columns.Add("Source IP", 140);
        _jobsView.SelectedIndexChanged += (_, _) => OnJobSelected();
        BuildJobsMenu();

        var jobsPanel = new Panel { Dock = DockStyle.Fill };
        jobsPanel.Controls.Add(_jobsView);
        jobsPanel.Controls.Add(_jobsHeader);

        var tabs = _tabs;
        var tabPreview = _tabPreview = new TabPage("Preview");
        tabPreview.Controls.Add(_preview);
        BuildLogMenu();
        var tabLog = new TabPage("Log");
        tabLog.Controls.Add(_log);
        tabs.TabPages.Add(tabPreview);
        tabs.TabPages.Add(tabLog);

        var right = _rightSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        right.Panel1.Controls.Add(jobsPanel);
        right.Panel2.Controls.Add(tabs);

        var main = _mainSplit = new SplitContainer { Dock = DockStyle.Fill };
        main.Panel1.Controls.Add(_printersView);
        main.Panel2.Controls.Add(right);

        Controls.Add(main);
        Controls.Add(tools);
    }

    // ---- lifecycle -------------------------------------------------------------------------

    private async Task InitializeAsync()
    {
        try
        {
            await _manager.InitializeAsync();
            RefreshAllPrinters();
            if (_printersView.Items.Count > 0) _printersView.Items[0].Selected = true;
        }
        catch (Exception ex) { AppLog.Error("Startup failed: " + ex); }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;
        if (e.CloseReason == CloseReason.UserClosing && !_exitRequested && _settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        _closing = true;
        _tray.Visible = false;
        _tray.Dispose();
        AppLog.Logged -= OnLogged;
        Cursor = Cursors.WaitCursor;
        _manager.Shutdown();   // removes secondary IPs; brief blocking is acceptable on exit
    }

    // ---- printer list ----------------------------------------------------------------------

    private PrinterListener? SelectedPrinter =>
        _printersView.SelectedItems.Count == 1 ? _printersView.SelectedItems[0].Tag as PrinterListener : null;

    private void RefreshAllPrinters()
    {
        var selected = SelectedPrinter;
        _printersView.BeginUpdate();
        _printersView.Items.Clear();
        foreach (var p in _manager.Printers)
        {
            var item = new ListViewItem(new string[4]) { Tag = p };
            Fill(item, p);
            _printersView.Items.Add(item);
            if (p == selected) item.Selected = true;
        }
        _printersView.EndUpdate();
        UpdateTrayText();
    }

    private void RefreshPrinter(PrinterListener p)
    {
        var item = _printersView.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag == p);
        if (item == null) { RefreshAllPrinters(); return; }
        Fill(item, p);
        UpdateTrayText();
        if (p == SelectedPrinter) UpdateJobsHeader(p);
    }

    private static void Fill(ListViewItem item, PrinterListener p)
    {
        var c = p.Config;
        item.SubItems[0].Text = c.Name;
        item.SubItems[1].Text = c.Label;
        item.SubItems[2].Text = c.Address;
        item.SubItems[3].Text = p.State switch
        {
            ListenerState.Listening => "Listening" + SimulationSuffix(c),
            ListenerState.Refusing => "Refusing connections",
            ListenerState.Error => "Error: " + p.StatusMessage,
            _ => p.StatusMessage
        };
        item.ForeColor = p.State switch
        {
            ListenerState.Listening => c.State == PrinterState.Online ? Color.SeaGreen : Color.DarkOrange,
            ListenerState.Refusing => Color.DarkOrange,
            ListenerState.Error => Color.Firebrick,
            _ => SystemColors.GrayText
        };
    }

    private static string StateLabel(PrinterState s) => s switch
    {
        PrinterState.PaperOut => "Paper out",
        PrinterState.CoverOpen => "Cover open",
        PrinterState.Offline => "Offline",
        _ => "Online"
    };

    private static string SimulationSuffix(PrinterConfig c)
    {
        var parts = new List<string>();
        if (c.State != PrinterState.Online) parts.Add(StateLabel(c.State));
        if (c.ConnectionDelayMs > 0) parts.Add($"{c.ConnectionDelayMs} ms delay");
        return parts.Count == 0 ? "" : " · " + string.Join(", ", parts);
    }

    private void OnPrinterSelected()
    {
        var p = SelectedPrinter;
        _btnEdit.Enabled = _btnRemove.Enabled = p != null;
        LoadJobs(p);
    }

    // ---- add / edit / remove ---------------------------------------------------------------

    private async Task AddPrinterAsync()
    {
        try
        {
            var cfg = new PrinterConfig { Name = $"Printer {_manager.Printers.Count + 1}" };
            using var dlg = new PrinterEditForm(cfg, _manager.Printers.Select(p => p.Config).ToList(), isNew: true);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var l = await _manager.AddAsync(dlg.Result);
            RefreshAllPrinters();
            SelectPrinter(l);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task EditSelectedAsync()
    {
        var p = SelectedPrinter;
        if (p == null) return;
        try
        {
            var others = _manager.Printers.Where(x => x != p).Select(x => x.Config).ToList();
            using var dlg = new PrinterEditForm(p.Config, others, isNew: false);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            await _manager.UpdateAsync(p, dlg.Result);
            RefreshPrinter(p);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task RemoveSelectedAsync()
    {
        var p = SelectedPrinter;
        if (p == null) return;
        var msg = $"Remove printer \"{p.Config.Name}\"?\n\nSaved jobs stay on disk in jobs\\{p.Config.JobFolder}.";
        if (p.Config.SecondaryIpAddedByApp) msg += $"\nThe secondary IP {p.Config.ListenIp} will be removed from the adapter.";
        if (MessageBox.Show(this, msg, "Remove printer", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            await _manager.RemoveAsync(p);
            RefreshAllPrinters();
            if (_printersView.Items.Count > 0 && SelectedPrinter == null) _printersView.Items[0].Selected = true;
            else if (_printersView.Items.Count == 0) LoadJobs(null);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SelectPrinter(PrinterListener p)
    {
        foreach (ListViewItem i in _printersView.Items) i.Selected = i.Tag == p;
    }

    // ---- jobs ------------------------------------------------------------------------------

    private void LoadJobs(PrinterListener? p)
    {
        _jobsView.BeginUpdate();
        _jobsView.Items.Clear();
        if (p != null)
            foreach (var j in p.Jobs) _jobsView.Items.Add(MakeJobItem(j));
        _jobsView.EndUpdate();
        UpdateJobsHeader(p);
        if (_jobsView.Items.Count > 0)
        {
            var last = _jobsView.Items[^1];
            last.Selected = true;
            last.EnsureVisible();
        }
        else _preview.Clear();
    }

    private void UpdateJobsHeader(PrinterListener? p) =>
        _jobsHeader.Text = p == null ? "  Jobs" : $"  Jobs - {p.Config.Name} ({p.Jobs.Count}) - {p.StatusMessage}";

    private static ListViewItem MakeJobItem(PrintJob j) =>
        new(new[] { j.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss.fff"), FormatSize(j.SizeBytes), j.SourceIp }) { Tag = j };

    private static string FormatSize(long b) => b < 1024 ? $"{b} B" : $"{b / 1024.0:0.#} KB";

    private void OnJobReceived(PrinterListener p, PrintJob job)
    {
        if (_closing) return;
        RefreshPrinter(p);
        if (!Visible || WindowState == FormWindowState.Minimized)
            _tray.ShowBalloonTip(3000, "Job received", $"{p.Config.Name}: {FormatSize(job.SizeBytes)} from {job.SourceIp}", ToolTipIcon.Info);
        if (p == SelectedPrinter)
        {
            var item = MakeJobItem(job);
            _jobsView.Items.Add(item);
            UpdateJobsHeader(p);
            if (p.Config.AutoOpen) { item.Selected = true; item.EnsureVisible(); _tabs.SelectedTab = _tabPreview; }
        }
        else if (p.Config.AutoOpen)
        {
            SelectPrinter(p);   // LoadJobs selects the newest job
            _tabs.SelectedTab = _tabPreview;
        }
    }

    private async void OnJobSelected()
    {
        var printer = SelectedPrinter;
        if (printer == null || _jobsView.SelectedItems.Count != 1) return;
        var job = (PrintJob)_jobsView.SelectedItems[0].Tag!;
        try { await _preview.ShowJobAsync(job, printer.Config); }
        catch (Exception ex) { AppLog.Error("Preview failed: " + ex.Message); }
    }

    private void OpenJobsFolder()
    {
        var dir = SelectedPrinter != null ? JobStore.FolderFor(SelectedPrinter.Config) : AppPaths.JobsDir;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    // ---- simulation menu -------------------------------------------------------------------

    private void BuildPrinterMenu()
    {
        var menu = new ContextMenuStrip();
        var stateItems = new List<(PrinterState State, ToolStripMenuItem Item)>();
        var stateMenu = new ToolStripMenuItem("Simulated state");
        foreach (var st in Enum.GetValues<PrinterState>())
        {
            var item = new ToolStripMenuItem(StateLabel(st));
            var state = st;
            item.Click += (_, _) => { if (SelectedPrinter is { } p) _manager.SetState(p, state); };
            stateItems.Add((st, item));
            stateMenu.DropDownItems.Add(item);
        }
        var refuse = new ToolStripMenuItem("Refuse connections");
        refuse.Click += async (_, _) =>
        {
            if (SelectedPrinter is not { } p) return;
            try { await _manager.SetRefuseConnectionsAsync(p, !p.Config.RefuseConnections); }
            catch (Exception ex) { ShowError(ex); }
        };
        var edit = new ToolStripMenuItem("Edit...", null, async (_, _) => await EditSelectedAsync());
        var remove = new ToolStripMenuItem("Remove", null, async (_, _) => await RemoveSelectedAsync());
        var folder = new ToolStripMenuItem("Open jobs folder", null, (_, _) => OpenJobsFolder());
        menu.Items.AddRange(new ToolStripItem[] { stateMenu, refuse, new ToolStripSeparator(), edit, remove, new ToolStripSeparator(), folder });
        menu.Opening += (_, e) =>
        {
            var p = SelectedPrinter;
            if (p == null) { e.Cancel = true; return; }
            foreach (var (st, item) in stateItems) item.Checked = p.Config.State == st;
            refuse.Checked = p.Config.RefuseConnections;
        };
        _printersView.ContextMenuStrip = menu;
        // Right-click selects the row under the cursor.
        _printersView.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && _printersView.GetItemAt(e.X, e.Y) is { } hit) hit.Selected = true;
        };
    }

    // ---- job actions -----------------------------------------------------------------------

    private void BuildJobsMenu()
    {
        var replay = new ToolStripMenuItem("Replay...", null, async (_, _) => await ReplaySelectedAsync());
        var delete = new ToolStripMenuItem("Delete", null, (_, _) => DeleteSelectedJobs()) { ShortcutKeyDisplayString = "Del" };
        var clear = new ToolStripMenuItem("Clear all jobs", null, (_, _) => ClearAllJobs());
        var reveal = new ToolStripMenuItem("Show file in folder", null, (_, _) => RevealSelectedJob());
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[] { replay, reveal, new ToolStripSeparator(), delete, clear });
        menu.Opening += (_, e) =>
        {
            var p = SelectedPrinter;
            if (p == null) { e.Cancel = true; return; }
            replay.Enabled = delete.Enabled = _jobsView.SelectedItems.Count > 0;
            reveal.Enabled = _jobsView.SelectedItems.Count == 1;
            clear.Enabled = p.Jobs.Count > 0;
        };
        _jobsView.ContextMenuStrip = menu;
        _jobsView.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) { DeleteSelectedJobs(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem i in _jobsView.Items) i.Selected = true; e.Handled = true; }
        };
    }

    private List<PrintJob> SelectedJobs() =>
        _jobsView.SelectedItems.Cast<ListViewItem>().Select(i => (PrintJob)i.Tag!).ToList();

    private void DeleteSelectedJobs()
    {
        var p = SelectedPrinter;
        var jobs = SelectedJobs();
        if (p == null || jobs.Count == 0) return;
        var what = jobs.Count == 1 ? "this job" : $"{jobs.Count} jobs";
        if (MessageBox.Show(this, $"Delete {what} from disk?", "Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _manager.DeleteJobs(p, jobs);
        LoadJobs(p);
    }

    private void ClearAllJobs()
    {
        var p = SelectedPrinter;
        if (p == null || p.Jobs.Count == 0) return;
        if (MessageBox.Show(this, $"Delete all {p.Jobs.Count} jobs of \"{p.Config.Name}\" from disk?", "Clear all jobs",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _manager.ClearJobs(p);
        LoadJobs(p);
    }

    private void RevealSelectedJob()
    {
        if (SelectedJobs() is [var job])
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{job.FilePath}\"") { UseShellExecute = true });
    }

    private async Task ReplaySelectedAsync()
    {
        var p = SelectedPrinter;
        var jobs = SelectedJobs();
        if (p == null || jobs.Count == 0) return;

        var host = p.Config.ListenIp == "0.0.0.0" ? "127.0.0.1" : p.Config.ListenIp;
        using var dlg = new ReplayForm(host, p.Config.Port, jobs.Count);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            foreach (var job in jobs)
            {
                await JobReplayer.SendAsync(job.FilePath, dlg.Host, dlg.Port);
                AppLog.Info($"Replayed {Path.GetFileName(job.FilePath)} to {dlg.Host}:{dlg.Port}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Replay to {dlg.Host}:{dlg.Port} failed: {ex.Message}");
            MessageBox.Show(this, $"Could not send to {dlg.Host}:{dlg.Port}\n\n{ex.Message}", "Replay", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---- tray & settings -------------------------------------------------------------------

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show VirtualPOSPrinter", null, (_, _) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _exitRequested = true; Close(); });
        _tray.Icon = Icon;
        _tray.Text = "VirtualPOSPrinter";
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();
        _tray.Visible = true;
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void UpdateTrayText()
    {
        int total = _manager.Printers.Count;
        int up = _manager.Printers.Count(p => p.State == ListenerState.Listening);
        var text = $"VirtualPOSPrinter - {up}/{total} listening";
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private ToolStripDropDownButton BuildSettingsMenu()
    {
        var startMin = new ToolStripMenuItem("Start minimized to tray") { CheckOnClick = true, Checked = _settings.StartMinimized };
        startMin.Click += (_, _) => { _settings.StartMinimized = startMin.Checked; _settings.Save(); };

        var closeTray = new ToolStripMenuItem("Close button hides to tray") { CheckOnClick = true, Checked = _settings.CloseToTray };
        closeTray.Click += (_, _) => { _settings.CloseToTray = closeTray.Checked; _settings.Save(); };

        bool registered = false;
        try { registered = StartupRegistration.IsEnabled(); } catch (Exception ex) { AppLog.Warn("Startup check failed: " + ex.Message); }
        var startup = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = registered };
        startup.Click += (_, _) =>
        {
            try
            {
                StartupRegistration.Set(startup.Checked);
                AppLog.Info(startup.Checked ? "Start with Windows enabled" : "Start with Windows disabled");
            }
            catch (Exception ex)
            {
                startup.Checked = !startup.Checked;
                ShowError(ex);
            }
        };

        var note = new ToolStripMenuItem("Note: secondary IPs need an elevated start") { Enabled = false };
        var button = new ToolStripDropDownButton("Settings");
        button.DropDownItems.AddRange(new ToolStripItem[] { startMin, closeTray, startup, new ToolStripSeparator(), note });
        return button;
    }

    // ---- log -------------------------------------------------------------------------------

    private void BuildLogMenu()
    {
        var copy = new ToolStripMenuItem("Copy", null, (_, _) => CopyLog(selectedOnly: true)) { ShortcutKeyDisplayString = "Ctrl+C" };
        var copyAll = new ToolStripMenuItem("Copy all", null, (_, _) => CopyLog(selectedOnly: false));
        var selectAll = new ToolStripMenuItem("Select all", null, (_, _) => SelectAllLog()) { ShortcutKeyDisplayString = "Ctrl+A" };
        var clear = new ToolStripMenuItem("Clear log", null, (_, _) => _log.Items.Clear());
        var openFile = new ToolStripMenuItem("Open log file", null, (_, _) => OpenLogFile());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[] { copy, copyAll, new ToolStripSeparator(), selectAll, clear, new ToolStripSeparator(), openFile });
        menu.Opening += (_, _) =>
        {
            copy.Enabled = _log.SelectedItems.Count > 0;
            copyAll.Enabled = selectAll.Enabled = clear.Enabled = _log.Items.Count > 0;
        };
        _log.ContextMenuStrip = menu;

        // Right-click selects the row under the cursor unless it is already part of the selection.
        _log.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            int i = _log.IndexFromPoint(e.Location);
            if (i >= 0 && !_log.SelectedIndices.Contains(i)) { _log.ClearSelected(); _log.SelectedIndex = i; }
        };
        _log.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C) { CopyLog(selectedOnly: true); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.A) { SelectAllLog(); e.Handled = true; }
        };
    }

    private void SelectAllLog()
    {
        _log.BeginUpdate();
        for (int i = 0; i < _log.Items.Count; i++) _log.SetSelected(i, true);
        _log.EndUpdate();
    }

    private void CopyLog(bool selectedOnly)
    {
        var lines = selectedOnly ? _log.SelectedItems.Cast<object>() : _log.Items.Cast<object>();
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
    }

    private static void OpenLogFile()
    {
        if (File.Exists(AppLog.LogFile))
            Process.Start(new ProcessStartInfo(AppLog.LogFile) { UseShellExecute = true });
    }

    private void OnLogged(LogEntry e)
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                _log.Items.Add(e.ToString());
                if (_log.Items.Count > 1000) _log.Items.RemoveAt(0);
                _log.TopIndex = _log.Items.Count - 1;
            });
        }
        catch (InvalidOperationException) { /* window closing */ }
    }

    private void ShowError(Exception ex)
    {
        AppLog.Error(ex.Message);
        MessageBox.Show(this, ex.Message, "VirtualPOSPrinter", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
