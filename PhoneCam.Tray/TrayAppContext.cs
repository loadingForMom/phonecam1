using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
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
    private VirtualCamFrameHub? _frameHub;

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
    private readonly bool _isAdmin;

    private const string HotspotSsid = "PhoneCamHotspot";
    private const string HotspotPsk = "PhoneCamPass123";
    private const int ControlPort = 39000;
    private const int UdpPort = 39010;
    private const string HotspotHost = "192.168.137.1";

    private static readonly string LogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhoneCam", "tray.log");
    private static readonly object LogLock = new();

    public TrayAppContext()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        _isAdmin = IsRunningAsAdmin();
        Log("Tray started");
        Log($"Admin: {(_isAdmin ? "yes" : "no")}");

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
        if (!_isAdmin)
        {
            _tray.ShowBalloonTip(2000, "PhoneCam", "Run as Administrator to start hotspot automatically.", ToolTipIcon.Warning);
        }

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
        _server.OnLog += LogSafe;
        _server.OnMediaStats += snap =>
            Log($"UDP: {snap.PacketsPerSec:F0} pkt/s, {(snap.BytesPerSec * 8 / 1000.0):F0} kbps, loss={snap.LossPerSec:F1}/s");

        _frameHub = new VirtualCamFrameHub(LogSafe);
        _frameHub.Start();

        _server.Start();
        LogServerDiagnostics();

        StartDecodeLoop();
    }

    private void Stop()
    {
        Log("Stop clicked");

        StopDecodeLoop();

        try
        {
            _frameHub?.Stop();
            _frameHub?.Dispose();
        }
        catch (Exception ex)
        {
            Log("Frame hub stop failed: " + ex);
        }
        finally
        {
            _frameHub = null;
        }

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

                        // Always publish the frame to the virtual camera hub
                        try
                        {
                            _frameHub?.UpdateFromBitmap(bmp);
                        }
                        catch (Exception ex)
                        {
                            Log("Frame hub update failed: " + ex.Message);
                        }

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

        try
        {
            _frameHub?.Stop();
            _frameHub?.Dispose();
        }
        catch (Exception ex)
        {
            Log("Frame hub stop failed: " + ex);
        }
        finally
        {
            _frameHub = null;
        }

        if (_server is not null)
        {
            _ = _server.StopAsync();
            _server = null;
        }

        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }

    private void LogSafe(string msg)
    {
        try
        {
            Log(msg);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Log failed: " + ex);
        }
    }

    private void Log(string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}";
        lock (LogLock)
        {
            try
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("File log failed: " + ex);
            }
        }
        try
        {
            Debug.WriteLine(line);
            _logForm?.AppendLine(line);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("UI log failed: " + ex);
        }
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
        _ble.OnLog += LogSafe;

        if (_ble.IsRunning)
        {
            _ble.Stop();
            _bleItem.Text = "Start BLE Advertise";
            return;
        }

        var nonce = Guid.NewGuid().ToString("N")[..8];
        var host = GetBestLanIpv4() ?? HotspotHost;
        var payload = $"ver=1;ssid={HotspotSsid};psk={HotspotPsk};tcp={ControlPort};udp={UdpPort};host={host};autostart=1;nonce={nonce}";
        await _ble.StartAsync(payload);
        _bleItem.Text = "Stop BLE Advertise";
    }

    private async Task StartBleProvisioningAsync()
    {
        _bleClient ??= new BleProvisioningClient(LogSafe, EnsureServerRunning, _isAdmin, ShowHotspotFallback);
        await _bleClient.StartAsync(HotspotSsid, HotspotPsk);
    }

    private void EnsureServerRunning()
    {
        if (_server is null)
        {
            Log("Server not running; starting before provisioning.");
            Start();
        }
    }

    private void ShowHotspotFallback(string reason)
    {
        try
        {
            _tray.ShowBalloonTip(4000, "PhoneCam", reason, ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Balloon failed: " + ex);
        }
    }

    private static bool IsRunningAsAdmin()
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private void LogServerDiagnostics()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        Log($"NET: {nic.Name} IPv4={addr.Address}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log("NET: interface scan failed: " + ex.Message);
        }

        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            var tcp = props.GetActiveTcpListeners();
            var udp = props.GetActiveUdpListeners();
            Log($"NET: TCP listeners={string.Join(", ", tcp.Select(ep => ep.ToString()))}");
            Log($"NET: UDP listeners={string.Join(", ", udp.Select(ep => ep.ToString()))}");
        }
        catch (Exception ex)
        {
            Log("NET: listener scan failed: " + ex.Message);
        }
    }

    private sealed class BleProvisioningClient
    {
        private readonly Action<string> _log;
        private readonly Action _ensureServerRunning;
        private readonly bool _isAdmin;
        private readonly Action<string> _showFallback;
        private BluetoothLEAdvertisementWatcher? _watcher;
        private bool _connecting;
        private bool _watcherRunning;
        private DateTime _nextGlobalAttemptUtc = DateTime.MinValue;
        private readonly Dictionary<ulong, DateTime> _nextDeviceAttemptUtc = new();
        private readonly object _gate = new();
        private readonly TimeSpan _deviceCooldown = TimeSpan.FromSeconds(30);
        private readonly TimeSpan _failureCooldown = TimeSpan.FromSeconds(10);
        private readonly TimeSpan _successCooldown = TimeSpan.FromSeconds(60);
        private readonly TimeSpan _fallbackCooldown = TimeSpan.FromSeconds(60);
        private DateTime _nextFallbackUtc = DateTime.MinValue;

        public BleProvisioningClient(Action<string> log, Action ensureServerRunning, bool isAdmin, Action<string> showFallback)
        {
            _log = log;
            _ensureServerRunning = ensureServerRunning;
            _isAdmin = isAdmin;
            _showFallback = showFallback;
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
                try
                {
                    if (_connecting) return;
                    if (DateTime.UtcNow < _nextGlobalAttemptUtc) return;

                    foreach (var uuid in args.Advertisement.ServiceUuids)
                    {
                        if (uuid == BleHandshakeService.ServiceUuid)
                        {
                            if (!CanAttempt(args.BluetoothAddress)) return;

                            _connecting = true;
                            StopWatcher();
                            var success = await HandleDeviceAsync(args.BluetoothAddress, ssid, psk);
                            _connecting = false;
                            ScheduleWatcherRestart(success ? _successCooldown : _failureCooldown);
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _connecting = false;
                    _log("BLE: watcher error " + ex);
                    ScheduleWatcherRestart(_failureCooldown);
                }
            };

            _watcher = watcher;
            StartWatcher();
            _log("BLE: scanning for phone advertisements");
        }

        private async Task<bool> HandleDeviceAsync(ulong address, string ssid, string psk)
        {
            _log($"BLE: phone detected addr={address}");
            _ensureServerRunning();

            var hotspotResult = StartHotspot(ssid, psk);
            if (hotspotResult != HotspotStartResult.Started)
            {
                _log("Hotspot: failed to start");
                if (hotspotResult == HotspotStartResult.Unsupported)
                {
                    MarkFallback(address);
                }
                else
                {
                    MarkFailure(address);
                }
            }

            try
            {
                using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
                if (device == null)
                {
                    _log("BLE: failed to connect to device");
                    MarkFailure(address);
                    return false;
                }

                var serviceResult = await device.GetGattServicesForUuidAsync(BleHandshakeService.ServiceUuid);
                if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
                {
                    _log($"BLE: service discovery failed status={serviceResult.Status}");
                    MarkFailure(address);
                    return false;
                }

                using var service = serviceResult.Services[0];
                var charResult = await service.GetCharacteristicsForUuidAsync(BleHandshakeService.HandshakeCharacteristicUuid);
                if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
                {
                    _log($"BLE: characteristic discovery failed status={charResult.Status}");
                    MarkFailure(address);
                    return false;
                }

                var characteristic = charResult.Characteristics[0];
                var writer = new DataWriter();
                var nonce = Guid.NewGuid().ToString("N")[..8];
                var host = hotspotResult == HotspotStartResult.Started ? HotspotHost : GetBestLanIpv4() ?? HotspotHost;
                var payload = $"ver=1;ssid={ssid};psk={psk};tcp={ControlPort};udp={UdpPort};host={host};autostart=1;nonce={nonce}";
                writer.WriteString(payload);
                var status = await characteristic.WriteValueAsync(writer.DetachBuffer());
                if (status == GattCommunicationStatus.Success)
                {
                    _log("BLE: credentials sent");
                    if (hotspotResult == HotspotStartResult.Started)
                    {
                        MarkSuccess(address);
                    }
                    return hotspotResult != HotspotStartResult.Failed;
                }
                else
                {
                    _log($"BLE: failed to send credentials status={status}");
                    MarkFailure(address);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log("BLE: provisioning error " + ex);
                MarkFailure(address);
                return false;
            }
        }

        private HotspotStartResult StartHotspot(string ssid, string psk)
        {
            try
            {
                if (!_isAdmin)
                {
                    _log("Hotspot: not running as admin; cannot start hostednetwork.");
                    _showFallback("Run as Administrator or enable Mobile Hotspot manually.");
                    return HotspotStartResult.Failed;
                }

                var support = IsHostedNetworkSupported();
                if (support == HostedNetworkSupport.No)
                {
                    _log("Hotspot: hosted network unsupported by driver.");
                    if (IsAppPackaged())
                    {
                        _log("Hotspot: app is packaged, but Mobile Hotspot API not wired.");
                    }
                    else
                    {
                        _log("Hotspot: app not packaged; WinRT tethering API unavailable.");
                    }
                    ShowFallbackOnce("Hosted network unsupported. Enable Mobile Hotspot manually.");
                    return HotspotStartResult.Unsupported;
                }

                if (support == HostedNetworkSupport.Unknown)
                {
                    _log("Hotspot: hosted network support unknown; attempting netsh.");
                }

                var config = RunNetsh($"wlan set hostednetwork mode=allow ssid=\"{ssid}\" key=\"{psk}\"");
                LogNetshResult("Hotspot: netsh set hostednetwork", config);
                if (config.ExitCode != 0)
                {
                    ReportHotspotFailure(config);
                    return HotspotStartResult.Failed;
                }

                var start = RunNetsh("wlan start hostednetwork");
                LogNetshResult("Hotspot: netsh start hostednetwork", start);
                if (start.ExitCode != 0)
                {
                    ReportHotspotFailure(start);
                    return HotspotStartResult.Failed;
                }

                _log($"Hotspot: started ssid={ssid}");
                return HotspotStartResult.Started;
            }
            catch (Exception ex)
            {
                _log("Hotspot: exception " + ex.Message);
                return HotspotStartResult.Failed;
            }
        }

        private static (int ExitCode, string StdOut, string StdErr) RunNetsh(string args)
        {
            var info = new ProcessStartInfo("netsh", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(info);
            if (proc == null)
            {
                return (-1, string.Empty, "Failed to start netsh process");
            }
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, stdout, stderr);
        }

        private void LogNetshResult(string prefix, (int ExitCode, string StdOut, string StdErr) result)
        {
            _log($"{prefix}: exit={result.ExitCode}");
            if (!string.IsNullOrWhiteSpace(result.StdOut))
            {
                _log($"{prefix} stdout: {result.StdOut.Trim()}");
            }
            if (!string.IsNullOrWhiteSpace(result.StdErr))
            {
                _log($"{prefix} stderr: {result.StdErr.Trim()}");
            }
        }

        private void ReportHotspotFailure((int ExitCode, string StdOut, string StdErr) result)
        {
            var combined = $"{result.StdOut}\n{result.StdErr}".ToLowerInvariant();
            if (combined.Contains("requires elevation") || combined.Contains("access is denied"))
            {
                _log("Hotspot: netsh failed (admin required).");
                _showFallback("Run as Administrator to start hotspot.");
                return;
            }
            if (combined.Contains("hosted network supported") && combined.Contains("no"))
            {
                _log("Hotspot: hosted network not supported.");
                _showFallback("Hosted network unsupported. Enable Mobile Hotspot manually.");
                return;
            }
            if (combined.Contains("wlan autoconfig") || combined.Contains("wlansvc") || combined.Contains("service has not been started"))
            {
                _log("Hotspot: WLAN AutoConfig service not running.");
                _showFallback("Start WLAN AutoConfig service or enable Mobile Hotspot manually.");
            }
        }

        private HostedNetworkSupport IsHostedNetworkSupported()
        {
            var res = RunNetsh("wlan show drivers");
            LogNetshResult("Hotspot: netsh show drivers", res);
            var lines = $"{res.StdOut}\n{res.StdErr}".Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.IndexOf("Hosted network supported", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("Поддержка размещенной сети", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (line.IndexOf("Yes", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        line.IndexOf("Да", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return HostedNetworkSupport.Yes;
                    }
                    if (line.IndexOf("No", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        line.IndexOf("Нет", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return HostedNetworkSupport.No;
                    }
                }
            }
            return HostedNetworkSupport.Unknown;
        }

        private static bool IsAppPackaged()
        {
            const int APPMODEL_ERROR_NO_PACKAGE = 15700;
            var length = 0;
            var result = GetCurrentPackageFullName(ref length, null);
            return result != APPMODEL_ERROR_NO_PACKAGE;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

        private bool CanAttempt(ulong address)
        {
            lock (_gate)
            {
                if (_nextDeviceAttemptUtc.TryGetValue(address, out var next) && DateTime.UtcNow < next)
                {
                    return false;
                }
                return true;
            }
        }

        private void MarkFailure(ulong address)
        {
            lock (_gate)
            {
                _nextDeviceAttemptUtc[address] = DateTime.UtcNow + _failureCooldown;
                _nextGlobalAttemptUtc = DateTime.UtcNow + _failureCooldown;
            }
        }

        private void MarkSuccess(ulong address)
        {
            lock (_gate)
            {
                _nextDeviceAttemptUtc[address] = DateTime.UtcNow + _deviceCooldown;
                _nextGlobalAttemptUtc = DateTime.UtcNow + _successCooldown;
            }
        }

        private void MarkFallback(ulong address)
        {
            lock (_gate)
            {
                _nextDeviceAttemptUtc[address] = DateTime.UtcNow + _fallbackCooldown;
                _nextGlobalAttemptUtc = DateTime.UtcNow + _fallbackCooldown;
            }
        }

        private void ShowFallbackOnce(string message)
        {
            lock (_gate)
            {
                if (DateTime.UtcNow < _nextFallbackUtc) return;
                _nextFallbackUtc = DateTime.UtcNow + _fallbackCooldown;
            }
            _showFallback(message);
        }

        private void StartWatcher()
        {
            if (_watcher == null || _watcherRunning) return;
            _watcher.Start();
            _watcherRunning = true;
        }

        private void StopWatcher()
        {
            if (_watcher == null || !_watcherRunning) return;
            _watcher.Stop();
            _watcherRunning = false;
        }

        private void ScheduleWatcherRestart(TimeSpan delay)
        {
            if (_watcher == null) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay);
                    StartWatcher();
                }
                catch (Exception ex)
                {
                    _log("BLE: watcher restart failed " + ex);
                }
            });
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

    private enum HostedNetworkSupport
    {
        Yes,
        No,
        Unknown
    }

    private enum HotspotStartResult
    {
        Started,
        Failed,
        Unsupported
    }

    private static string? GetBestLanIpv4()
    {
        NetworkInterface? wifi = null;
        NetworkInterface? ethernet = null;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && wifi == null)
            {
                wifi = nic;
            }
            else if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet && ethernet == null)
            {
                ethernet = nic;
            }
        }

        var chosen = wifi ?? ethernet;
        if (chosen == null) return null;

        foreach (var addr in chosen.GetIPProperties().UnicastAddresses)
        {
            if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return addr.Address.ToString();
            }
        }

        return null;
    }
}
