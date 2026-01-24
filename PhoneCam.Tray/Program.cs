using System;
using System.Windows.Forms;

namespace PhoneCam.Tray;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        try
        {
            FfmpegBootstrap.Init();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Failed to load FFmpeg native libraries.\n\n" + ex,
                "PhoneCam",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }
}