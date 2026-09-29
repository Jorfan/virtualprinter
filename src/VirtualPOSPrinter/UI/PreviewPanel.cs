using System.Diagnostics;
using System.Globalization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using VirtualPOSPrinter.EscPos;
using VirtualPOSPrinter.Models;
using VirtualPOSPrinter.Services;

namespace VirtualPOSPrinter.UI;

/// <summary>Job preview: rendered receipt (WebView2) or hex/text dump, with code page override and PDF export.</summary>
public sealed class PreviewPanel : UserControl
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly TextBox _hex = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Font = new Font("Consolas", 9.5f), Dock = DockStyle.Fill, BackColor = SystemColors.Window
    };
    private readonly Label _message = new() { Dock = DockStyle.Top, Height = 0, ForeColor = Color.Firebrick, Padding = new Padding(6, 4, 6, 4), AutoSize = false };
    private readonly ToolStripButton _btnRendered = new("Rendered") { CheckOnClick = false, Checked = true };
    private readonly ToolStripButton _btnHex = new("Hex / text");
    private readonly ToolStripComboBox _codePage = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly ToolStripButton _btnPdf = new("Export to PDF and open") { Enabled = false };

    private PrintJob? _job;
    private PrinterConfig? _printer;
    private bool _webReady;
    private bool _hexMode;
    private int _version;
    private bool _initStarted;

    public PreviewPanel()
    {
        Dock = DockStyle.Fill;

        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        _btnRendered.Click += async (_, _) => await SetModeAsync(hex: false);
        _btnHex.Click += async (_, _) => await SetModeAsync(hex: true);
        _btnPdf.Click += async (_, _) => await ExportPdfAsync();
        _codePage.Items.Add("Code page: auto (ESC t)");
        foreach (var (name, _) in CodePages.Available) _codePage.Items.Add("Force " + name);
        _codePage.SelectedIndex = 0;
        _codePage.SelectedIndexChanged += async (_, _) => await RefreshAsync();
        bar.Items.AddRange(new ToolStripItem[] { _btnRendered, _btnHex, new ToolStripSeparator(), _codePage, new ToolStripSeparator(), _btnPdf });

        Controls.Add(_web);
        Controls.Add(_hex);
        Controls.Add(_message);
        Controls.Add(bar);
        UpdateModeUi();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_initStarted) return;
        _initStarted = true;
        _ = InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VirtualPOSPrinter", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webReady = true;
            UpdateModeUi();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _hexMode = true;
            ShowMessage("Rendered preview unavailable: the WebView2 runtime could not be started (" + ex.Message +
                        "). Install the Evergreen WebView2 Runtime from Microsoft. Showing hex/text instead.");
            AppLog.Error("WebView2 init failed: " + ex.Message);
            UpdateModeUi();
            await RefreshAsync();
        }
    }

    // ---- public API ------------------------------------------------------------------------

    public async Task ShowJobAsync(PrintJob job, PrinterConfig printer)
    {
        _job = job;
        _printer = printer;
        await RefreshAsync();
    }

    public void Clear()
    {
        _job = null;
        _printer = null;
        _version++;
        _hex.Clear();
        if (_webReady) _web.CoreWebView2.NavigateToString("<html><body style=\"background:#d8dadd\"></body></html>");
        UpdateModeUi();
    }

    // ---- rendering -------------------------------------------------------------------------

    private int? SelectedCodePage => _codePage.SelectedIndex <= 0 ? null : CodePages.Available[_codePage.SelectedIndex - 1].CodePage;

    private async Task SetModeAsync(bool hex)
    {
        if (!hex && !_webReady) return;
        _hexMode = hex;
        UpdateModeUi();
        await RefreshAsync();
    }

    private void UpdateModeUi()
    {
        bool showHex = _hexMode || !_webReady;
        _web.Visible = !showHex;
        _hex.Visible = showHex;
        _btnRendered.Checked = !showHex;
        _btnHex.Checked = showHex;
        _btnRendered.Enabled = _webReady;
        _btnPdf.Enabled = _webReady && _job != null;
        _codePage.Enabled = !showHex;
    }

    private void ShowMessage(string text)
    {
        _message.Text = text;
        _message.Height = string.IsNullOrEmpty(text) ? 0 : 44;
    }

    private async Task RefreshAsync()
    {
        int version = ++_version;
        var job = _job;
        var printer = _printer;
        UpdateModeUi();
        if (job == null || printer == null) return;

        try
        {
            if (_hexMode || !_webReady)
            {
                var header = $"{job.FilePath}\r\n{job.SizeBytes} bytes from {job.SourceIp} at {job.ReceivedAt:yyyy-MM-dd HH:mm:ss.fff}";
                var text = await Task.Run(() => HexDump.FromFile(job.FilePath, header));
                if (version == _version) _hex.Text = text;
                return;
            }

            var mm = printer.PaperWidthMm;
            var cp = SelectedCodePage;
            var html = await Task.Run(() =>
            {
                var bytes = File.ReadAllBytes(job.FilePath);
                var parsed = EscPosParser.Parse(bytes, new ParserOptions { PaperWidthMm = mm, CodePageOverride = cp });
                return HtmlRenderer.Render(parsed, new RenderOptions { PaperWidthMm = mm, Title = Path.GetFileName(job.FilePath) });
            });
            if (version != _version) return;
            Navigate(html);
        }
        catch (Exception ex)
        {
            AppLog.Error("Preview failed: " + ex.Message);
            if (version == _version) _hex.Text = "Could not show job: " + ex.Message;
        }
    }

    private void Navigate(string html)
    {
        if (html.Length < 1_500_000)
        {
            _web.CoreWebView2.NavigateToString(html);   // NavigateToString is limited to ~2 MB
            return;
        }
        var file = Path.Combine(Path.GetTempPath(), "VirtualPOSPrinter-preview.html");
        File.WriteAllText(file, html);
        _web.CoreWebView2.Navigate(new Uri(file).AbsoluteUri);
    }

    // ---- PDF -------------------------------------------------------------------------------

    private async Task ExportPdfAsync()
    {
        var job = _job;
        var printer = _printer;
        if (!_webReady || job == null || printer == null) return;
        try
        {
            if (_hexMode) await SetModeAsync(hex: false);
            await Task.Delay(300);   // let a pending navigation finish

            var core = _web.CoreWebView2;
            double Num(string s) => double.TryParse(s.Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            double paperPx = Num(await core.ExecuteScriptAsync("document.querySelector('.paper') ? document.querySelector('.paper').offsetWidth : 0"));
            double wrapPx = Num(await core.ExecuteScriptAsync("document.querySelector('.wrap') ? document.querySelector('.wrap').scrollHeight : 0"));
            if (paperPx <= 0) { MessageBox.Show(this, "Nothing to export.", "Export to PDF"); return; }

            // Scale the page so the paper element fills the sheet: sheet width = paper width in inches.
            double widthIn = printer.PaperWidthMm / 25.4;
            double scale = widthIn * 96 / paperPx;
            var settings = core.Environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.ScaleFactor = scale;
            settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 0;
            settings.PageWidth = widthIn;
            settings.PageHeight = Math.Min(200, Math.Max(1, wrapPx * scale / 96 + 0.3));

            var pdf = Path.ChangeExtension(job.FilePath, ".pdf");
            bool ok = await core.PrintToPdfAsync(pdf, settings);
            if (!ok) { MessageBox.Show(this, "WebView2 could not create the PDF.", "Export to PDF", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            AppLog.Info("Exported " + pdf);
            Process.Start(new ProcessStartInfo(pdf) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("PDF export failed: " + ex.Message);
            MessageBox.Show(this, "PDF export failed: " + ex.Message, "Export to PDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
