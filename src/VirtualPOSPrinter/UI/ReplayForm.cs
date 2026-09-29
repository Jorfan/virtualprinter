namespace VirtualPOSPrinter.UI;

/// <summary>Asks where to send a saved job. Defaults to the printer that received it.</summary>
public sealed class ReplayForm : Form
{
    private readonly TextBox _host = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Dock = DockStyle.Left, Width = 90 };

    public string Host => _host.Text.Trim();
    public int Port => (int)_port.Value;

    public ReplayForm(string host, int port, int jobCount)
    {
        Text = "Replay job" + (jobCount > 1 ? "s" : "");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 150);

        _host.Text = host;
        _port.Value = port;

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 100, ColumnCount = 2, Padding = new Padding(12, 12, 12, 0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.Controls.Add(new Label { Text = jobCount > 1 ? $"Send {jobCount} jobs to:" : "Send job to:", AutoSize = true }, 0, 0);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, 0)!, 2);
        grid.Controls.Add(new Label { Text = "Host", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, 1);
        grid.Controls.Add(_host, 1, 1);
        grid.Controls.Add(new Label { Text = "Port", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, 2);
        grid.Controls.Add(_port, 1, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        var ok = new Button { Text = "Send", Width = 90 };
        ok.Click += (_, _) =>
        {
            if (Host.Length == 0) { MessageBox.Show(this, "Enter a host or IP address.", Text); return; }
            DialogResult = DialogResult.OK;
            Close();
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(grid);
        Controls.Add(buttons);
    }
}
