using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PhoneCam.Core;

namespace PhoneCam.Tray;

public sealed class TrayAppContext : ApplicationContext
{
    private PhoneCamServer? _server;

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _showVideoItem;

    private bool _running;

    private VideoForm? _videoForm;
    private CancellationTokenSource? _decodeCts;
    private Task? _decodeTask;

    private static readonly string LogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhoneCam", "tray.log");

    public TrayAppContext()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        Log("Tray started");

        _startItem = new ToolStripMenuItem("Start", null, (_, _) => Start());
        _stopItem = new ToolStripMenuItem("Stop", null, (_, _) => Stop()) { Enabled = false };
        _showVideoItem = new ToolStripMenuItem("Show Video", null, (_, _) => ShowVideo()) { Enabled = false };

        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => Exit());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(_showVideoItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _tray = new NotifyIcon
        {
            Visible = true,
            Text = "PhoneCam — stopped",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu
        };

        _tray.DoubleClick += (_, _) => Toggle();
        _tray.ShowBalloonTip(1000, "PhoneCam", "Tray started", ToolTipIcon.Info);
    }

    private void Toggle()
    {
        if (_running) Stop();
        else Start();
    }

    private void Start()
    {
        if (_server is not null) return;

        _running = true;
        _startItem.Enabled = false;
        _stopItem.Enabled = true;
        _showVideoItem.Enabled = true;

        _tray.Text = "PhoneCam — running";
        _tray.ShowBalloonTip(1000, "PhoneCam", "Running", ToolTipIcon.Info);
        Log("Start clicked");

        _server = new PhoneCamServer();
        _server.OnLog += Log;
        _server.OnMediaStats += snap =>
            Log($"UDP: {snap.PacketsPerSec:F0} pkt/s, {(snap.BytesPerSec * 8 / 1000.0):F0} kbps, loss={snap.LossPerSec:F1}/s");

        _server.Start();

        StartDecodeLoop();
    }

    private void Stop()
    {
        Log("Stop clicked");

        StopDecodeLoop();

        if (_server is not null)
        {
            _ = _server.StopAsync(); // не блокируем UI
            _server = null;
        }

        _running = false;
        _startItem.Enabled = true;
        _stopItem.Enabled = false;
        _showVideoItem.Enabled = false;

        _tray.Text = "PhoneCam — stopped";
        _tray.ShowBalloonTip(1000, "PhoneCam", "Stopped", ToolTipIcon.Info);
    }

    private void ShowVideo()
    {
        if (_videoForm is null || _videoForm.IsDisposed)
        {
            _videoForm = new VideoForm();
            _videoForm.FormClosed += (_, _) => _videoForm = null;
            _videoForm.Show();
        }
        else
        {
            _videoForm.WindowState = FormWindowState.Normal;
            _videoForm.BringToFront();
            _videoForm.Activate();
        }
    }

    private void StartDecodeLoop()
    {
        if (_server is null) return;
        if (_decodeTask is not null) return;

        _decodeCts = new CancellationTokenSource();
        var ct = _decodeCts.Token;

        _decodeTask = Task.Run(async () =>
        {
            var fps = new FpsCounter();
            using var decoder = new H264FfmpegDecoder();
            decoder.OnLog += s => Log("DEC: " + s);

            try
            {
                await foreach (var au in _server.Frames.ReadAllAsync(ct))
                {
                    foreach (var bmp in decoder.DecodeToBitmaps(au))
                    {
                        fps.OnFrame();
                        var form = _videoForm;

                        if (form is not null && !form.IsDisposed)
                        {
                            // UI thread
                            form.BeginInvoke(() =>
                            {
                                form.ShowFrame(bmp, fps.CurrentFps);
                            });
                        }
                        else
                        {
                            // если окна нет — не течём по памяти
                            bmp.Dispose();
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log("Decode loop error: " + ex);
            }
        }, ct);
    }

    private void StopDecodeLoop()
    {
        try
        {
            _decodeCts?.Cancel();
        }
        catch { /* ignore */ }

        _decodeCts = null;
        _decodeTask = null;

        // окно можно оставить; если хочешь — можно закрывать:
        // _videoForm?.Close();
    }

    private void Exit()
    {
        Log("Exit clicked");

        StopDecodeLoop();

        if (_server is not null)
        {
            _ = _server.StopAsync();
            _server = null;
        }

        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }

    private static void Log(string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}";
        File.AppendAllText(LogPath, line + Environment.NewLine);
        Debug.WriteLine(line);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tray.Dispose();
        base.Dispose(disposing);
    }

    private sealed class FpsCounter
    {
        private int _frames;
        private long _t0 = Environment.TickCount64;
        public double CurrentFps { get; private set; }

        public void OnFrame()
        {
            _frames++;
            var dt = Environment.TickCount64 - _t0;
            if (dt >= 1000)
            {
                CurrentFps = _frames * 1000.0 / dt;
                _frames = 0;
                _t0 = Environment.TickCount64;
            }
        }
    }
}