using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PhoneCam.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace PhoneCam.Tray;

public sealed class TrayAppContext : ApplicationContext
{
    private PhoneCamServer? _server;

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _showVideoItem;
    private readonly ToolStripMenuItem _debugItem;
    private readonly ToolStripMenuItem _bleItem;

    private bool _running;

    private VideoForm? _videoForm;
    private LogForm? _logForm;
    private CancellationTokenSource? _decodeCts;
    private Task? _decodeTask;
    private BleHandshakeService? _ble;
    private BleProvisioningClient? _bleClient;

    private const string HotspotSsid = "PhoneCamHotspot";
    private const string HotspotPsk = "PhoneCamPass123";

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
        _debugItem = new ToolStripMenuItem("Debug Console", null, (_, _) => ShowDebugConsole());
        _bleItem = new ToolStripMenuItem("Start BLE Advertise", null, async (_, _) => await ToggleBleAsync());

        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => Exit());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(_showVideoItem);
        menu.Items.Add(_debugItem);
        menu.Items.Add(_bleItem);
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

        _ = StartBleProvisioningAsync();
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
        _ble?.Stop();

        if (_server is not null)
        {
            _ = _server.StopAsync();
            _server = null;
        }

        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }

    private void Log(string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}";
        File.AppendAllText(LogPath, line + Environment.NewLine);
        Debug.WriteLine(line);
        _logForm?.AppendLine(line);
    }

    private void ShowDebugConsole()
    {
        if (_logForm is null || _logForm.IsDisposed)
        {
            _logForm = new LogForm();
            _logForm.FormClosed += (_, _) => _logForm = null;
            _logForm.Show();
        }
        else
        {
            _logForm.WindowState = FormWindowState.Normal;
            _logForm.BringToFront();
            _logForm.Activate();
        }
    }

    private async Task ToggleBleAsync()
    {
        _ble ??= new BleHandshakeService();
        _ble.OnLog += Log;

        if (_ble.IsRunning)
        {
            _ble.Stop();
            _bleItem.Text = "Start BLE Advertise";
            return;
        }

        var payload = $"pcam;ver=1;tcp=39000;udp=39010;ssid={HotspotSsid};psk={HotspotPsk}";
        await _ble.StartAsync(payload);
        _bleItem.Text = "Stop BLE Advertise";
    }

    private async Task StartBleProvisioningAsync()
    {
        _bleClient ??= new BleProvisioningClient(Log);
        await _bleClient.StartAsync(HotspotSsid, HotspotPsk);
    }

    private sealed class BleProvisioningClient
    {
        private readonly Action<string> _log;
        private BluetoothLEAdvertisementWatcher? _watcher;
        private bool _connecting;

        public BleProvisioningClient(Action<string> log)
        {
            _log = log;
        }

        public async Task StartAsync(string ssid, string psk)
        {
            if (_watcher != null) return;

            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            watcher.Received += async (_, args) =>
            {
                if (_connecting) return;

                foreach (var uuid in args.Advertisement.ServiceUuids)
                {
                    if (uuid == BleHandshakeService.ServiceUuid)
                    {
                        _connecting = true;
                        watcher.Stop();
                        await HandleDeviceAsync(args.BluetoothAddress, ssid, psk);
                        _connecting = false;
                        watcher.Start();
                        break;
                    }
                }
            };

            watcher.Start();
            _watcher = watcher;
            _log("BLE: scanning for phone advertisements");
        }

        private async Task HandleDeviceAsync(ulong address, string ssid, string psk)
        {
            _log($"BLE: phone detected addr={address}");

            if (!StartHotspot(ssid, psk))
            {
                _log("Hotspot: failed to start");
                return;
            }

            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (device == null)
            {
                _log("BLE: failed to connect to device");
                return;
            }

            var serviceResult = await device.GetGattServicesForUuidAsync(BleHandshakeService.ServiceUuid);
            if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
            {
                _log($"BLE: service discovery failed status={serviceResult.Status}");
                return;
            }

            var service = serviceResult.Services[0];
            var charResult = await service.GetCharacteristicsForUuidAsync(BleHandshakeService.HandshakeCharacteristicUuid);
            if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
            {
                _log($"BLE: characteristic discovery failed status={charResult.Status}");
                return;
            }

            var characteristic = charResult.Characteristics[0];
            var writer = new DataWriter();
            writer.WriteString($"{ssid}|{psk}");
            var status = await characteristic.WriteValueAsync(writer.DetachBuffer());
            _log(status == GattCommunicationStatus.Success
                ? "BLE: credentials sent"
                : $"BLE: failed to send credentials status={status}");
        }

        private bool StartHotspot(string ssid, string psk)
        {
            try
            {
                var config = new ProcessStartInfo("netsh", $"wlan set hostednetwork mode=allow ssid=\"{ssid}\" key=\"{psk}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var proc = Process.Start(config))
                {
                    proc?.WaitForExit();
                    if (proc?.ExitCode != 0)
                    {
                        _log("Hotspot: netsh set hostednetwork failed");
                        return false;
                    }
                }

                var start = new ProcessStartInfo("netsh", "wlan start hostednetwork")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var proc = Process.Start(start))
                {
                    proc?.WaitForExit();
                    if (proc?.ExitCode != 0)
                    {
                        _log("Hotspot: netsh start hostednetwork failed");
                        return false;
                    }
                }

                _log($"Hotspot: started ssid={ssid}");
                return true;
            }
            catch (Exception ex)
            {
                _log("Hotspot: exception " + ex.Message);
                return false;
            }
        }
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
