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
            FfmpegBootstrap.LoadOrThrow();
        }
        catch (Exception ex)
        {
            var report = FfmpegBootstrap.SelfCheck();
            MessageBox.Show(ex + Environment.NewLine + Environment.NewLine + report, "Failed to load FFmpeg");
            return;
        
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }
}
