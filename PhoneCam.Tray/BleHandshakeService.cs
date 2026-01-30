using System;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace PhoneCam.Tray;

public sealed class BleHandshakeService
{
    public static readonly Guid ServiceUuid = Guid.Parse("0000feed-0000-1000-8000-00805f9b34fb");
    public static readonly Guid HandshakeCharacteristicUuid = Guid.Parse("0000feed-0001-1000-8000-00805f9b34fb");

    public event Action<string>? OnLog;

    private GattServiceProvider? _provider;
    private GattLocalCharacteristic? _handshakeCharacteristic;
    private string _payload = string.Empty;

    public bool IsRunning => _provider != null;

    public async Task StartAsync(string payload)
    {
        if (_provider != null) return;

        _payload = payload;
        var result = await GattServiceProvider.CreateAsync(ServiceUuid);
        if (result.Error != BluetoothError.Success)
        {
            Log($"BLE: GattServiceProvider.CreateAsync error={result.Error}");
            return;
        }

        _provider = result.ServiceProvider;
        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read,
            ReadProtectionLevel = GattProtectionLevel.Plain,
            UserDescription = "PhoneCam handshake"
        };

        var charResult = await _provider.Service.CreateCharacteristicAsync(HandshakeCharacteristicUuid, parameters);
        if (charResult.Error != BluetoothError.Success)
        {
            Log($"BLE: CreateCharacteristicAsync error={charResult.Error}");
            _provider = null;
            return;
        }

        _handshakeCharacteristic = charResult.Characteristic;
        _handshakeCharacteristic.ReadRequested += OnReadRequested;

        _provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsDiscoverable = true,
            IsConnectable = true
        });

        Log("BLE: advertising started");
    }

    public void Stop()
    {
        try
        {
            if (_handshakeCharacteristic != null)
                _handshakeCharacteristic.ReadRequested -= OnReadRequested;
            _provider?.StopAdvertising();
        }
        catch (Exception ex)
        {
            Log("BLE: stop error " + ex.Message);
        }
        finally
        {
            _handshakeCharacteristic = null;
            _provider = null;
            Log("BLE: advertising stopped");
        }
    }

    private async void OnReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request == null) return;

            var writer = new DataWriter();
            writer.WriteBytes(Encoding.UTF8.GetBytes(_payload));
            request.RespondWithValue(writer.DetachBuffer());
            Log("BLE: handshake read served");
        }
        catch (Exception ex)
        {
            Log("BLE: read error " + ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void Log(string msg) => OnLog?.Invoke(msg);
}
