using System.Net;
using VirtualPOSPrinter.EscPos;
using VirtualPOSPrinter.Models;
using VirtualPOSPrinter.Services;

namespace VirtualPOSPrinter.UI;

public sealed class PrinterEditForm : Form
{
    private readonly PrinterConfig _cfg;
    private readonly IReadOnlyList<PrinterConfig> _others;

    private readonly TextBox _name = new();
    private readonly ComboBox _label = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly RadioButton _rbExisting = new() { Text = "Bind to an existing local IP", AutoSize = true };
    private readonly RadioButton _rbSecondary = new() { Text = "Add a secondary IP to a network adapter (admin)", AutoSize = true };
    private readonly ComboBox _localIp = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _adapter = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _secIp = new();
    private readonly TextBox _mask = new();
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535 };
    private readonly ComboBox _paper = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _state = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _delay = new() { Minimum = 0, Maximum = 60000, Increment = 100 };
    private readonly CheckBox _refuse = new() { Text = "Refuse connections (port closed)", AutoSize = true };
    private readonly CheckBox _autoOpen = new() { Text = "Auto-open incoming jobs", AutoSize = true };
    private readonly CheckBox _enabled = new() { Text = "Enabled (listening)", AutoSize = true };

    /// <param name="cfg">Config to edit (a copy is edited; the original is untouched).</param>
    /// <param name="others">The other printers, for port/IP conflict checks.</param>
    public PrinterEditForm(PrinterConfig cfg, IReadOnlyList<PrinterConfig> others, bool isNew)
    {
        _cfg = cfg.Clone();
        _others = others;

        Text = isNew ? "Add printer" : "Edit printer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 500);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12),
            AutoSize = false
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control c)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 6) });
            c.Dock = DockStyle.Fill;
            grid.Controls.Add(c);
        }
        void Wide(Control c)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(c);
            grid.SetColumnSpan(c, 2);
        }

        _label.Items.AddRange(new object[] { "Receipt", "Kitchen" });
        _paper.Items.AddRange(new object[] { "80 mm", "58 mm" });
        _state.Items.AddRange(new object[] { "Online", "Paper out", "Cover open", "Offline" });
        _localIp.Items.Add("0.0.0.0 (all interfaces)");
        _localIp.Items.Add("127.0.0.1");
        foreach (var ip in NetworkService.GetLocalIPv4().Where(i => i != "127.0.0.1")) _localIp.Items.Add(ip);
        foreach (var a in NetworkService.GetAdapters()) _adapter.Items.Add(a);

        Row("Name", _name);
        Row("Label", _label);
        Wide(_rbExisting);
        Row("   Local IP", _localIp);
        Wide(_rbSecondary);
        Row("   Adapter", _adapter);
        Row("   New IP", _secIp);
        Row("   Subnet mask", _mask);
        Row("Port", _port);
        Row("Paper width", _paper);
        Row("Simulated state", _state);
        Row("Connection delay (ms)", _delay);
        Wide(_refuse);
        Wide(_autoOpen);
        Wide(_enabled);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        var ok = new Button { Text = "OK", Width = 90 };
        ok.Click += (_, _) => OnOk();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(grid);
        Controls.Add(buttons);

        _rbExisting.CheckedChanged += (_, _) => UpdateModeUi();
        _rbSecondary.CheckedChanged += (_, _) => UpdateModeUi();
        Load += (_, _) => Populate();
    }

    public PrinterConfig Result => _cfg;

    private void Populate()
    {
        _name.Text = _cfg.Name;
        _label.SelectedItem = _cfg.Label == "Kitchen" ? "Kitchen" : "Receipt";
        _port.Value = Math.Clamp(_cfg.Port, 1, 65535);
        _paper.SelectedIndex = _cfg.PaperWidthMm == 58 ? 1 : 0;
        _autoOpen.Checked = _cfg.AutoOpen;
        _enabled.Checked = _cfg.Enabled;
        _state.SelectedIndex = (int)_cfg.State;
        _delay.Value = Math.Clamp(_cfg.ConnectionDelayMs, 0, 60000);
        _refuse.Checked = _cfg.RefuseConnections;
        _mask.Text = string.IsNullOrWhiteSpace(_cfg.SubnetMask) ? "255.255.255.0" : _cfg.SubnetMask;

        if (_cfg.IpMode == IpMode.SecondaryIp)
        {
            _rbSecondary.Checked = true;
            _secIp.Text = _cfg.ListenIp;
            _localIp.Text = "0.0.0.0 (all interfaces)";
        }
        else
        {
            _rbExisting.Checked = true;
            _localIp.Text = _cfg.ListenIp == "0.0.0.0" ? "0.0.0.0 (all interfaces)" : _cfg.ListenIp;
        }

        var idx = _adapter.Items.Cast<AdapterInfo>().ToList().FindIndex(a => a.Name == _cfg.AdapterName);
        if (idx >= 0) _adapter.SelectedIndex = idx;
        else if (_adapter.Items.Count > 0) _adapter.SelectedIndex = 0;
        UpdateModeUi();
    }

    private void UpdateModeUi()
    {
        bool sec = _rbSecondary.Checked;
        _localIp.Enabled = !sec;
        _adapter.Enabled = _secIp.Enabled = _mask.Enabled = sec;
    }

    private void OnOk()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { Warn("Please enter a name."); return; }

        var draft = _cfg.Clone();
        draft.Name = _name.Text.Trim();
        draft.Label = (string)_label.SelectedItem!;
        draft.Port = (int)_port.Value;
        draft.PaperWidthMm = _paper.SelectedIndex == 1 ? 58 : 80;
        draft.AutoOpen = _autoOpen.Checked;
        draft.Enabled = _enabled.Checked;
        draft.State = (PrinterState)_state.SelectedIndex;
        draft.ConnectionDelayMs = (int)_delay.Value;
        draft.RefuseConnections = _refuse.Checked;

        if (_rbSecondary.Checked)
        {
            draft.IpMode = IpMode.SecondaryIp;
            if (!IPAddress.TryParse(_secIp.Text.Trim(), out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            { Warn("Enter a valid IPv4 address for the new IP."); return; }
            if (!IPAddress.TryParse(_mask.Text.Trim(), out _)) { Warn("Enter a valid subnet mask, e.g. 255.255.255.0."); return; }
            if (_adapter.SelectedItem is not AdapterInfo adapter) { Warn("Select a network adapter."); return; }
            draft.ListenIp = ip.ToString();
            draft.SubnetMask = _mask.Text.Trim();
            draft.AdapterName = adapter.Name;

            if (!NetworkService.IsElevated())
            {
                Warn("Adding a secondary IP address requires administrator rights.\n\n" +
                     "Close VirtualPOSPrinter and start it again with \"Run as administrator\", " +
                     "or switch to \"Bind to an existing local IP\".");
                return;
            }
        }
        else
        {
            draft.IpMode = IpMode.ExistingIp;
            var text = _localIp.Text.Trim();
            var first = text.Split(' ')[0];
            if (!IPAddress.TryParse(first, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            { Warn("Enter a valid IPv4 address."); return; }
            draft.ListenIp = ip.ToString();
        }

        var clash = _others.FirstOrDefault(o => o.Port == draft.Port &&
            (o.ListenIp == draft.ListenIp || o.ListenIp == "0.0.0.0" || draft.ListenIp == "0.0.0.0"));
        if (clash != null)
        {
            Warn($"Printer \"{clash.Name}\" already uses port {clash.Port} on {clash.ListenIp}. Choose another IP or port.");
            return;
        }

        _cfg.CopyFrom(draft);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Warn(string msg) => MessageBox.Show(this, msg, "VirtualPOSPrinter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
