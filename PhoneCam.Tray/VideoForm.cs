using System.Drawing;
using System.Windows.Forms;

public sealed class VideoForm : Form
{
    private readonly PictureBox _pb = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Label _lbl = new() { Dock = DockStyle.Top, Height = 24 };

    public double LastFps { get; private set; }

    public VideoForm()
    {
        Text = "PhoneCam MVP";
        Width = 1000;
        Height = 700;
        Controls.Add(_pb);
        Controls.Add(_lbl);
    }

    public void ShowFrame(Bitmap bmp, double fps)
    {
        LastFps = fps;
        _lbl.Text = $"FPS: {fps:F1}   {bmp.Width}x{bmp.Height}";

        var old = _pb.Image;
        _pb.Image = bmp;
        old?.Dispose(); // MVP: чтобы не текло
    }
}