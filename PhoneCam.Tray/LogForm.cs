using System;
using System.Windows.Forms;

namespace PhoneCam.Tray;

public sealed class LogForm : Form
{
    private readonly TextBox _txt = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical
    };

    public LogForm()
    {
        Text = "PhoneCam Debug Console";
        Width = 900;
        Height = 600;
        Controls.Add(_txt);
    }

    public void AppendLine(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLine(line));
            return;
        }

        _txt.AppendText(line + Environment.NewLine);
    }
}
