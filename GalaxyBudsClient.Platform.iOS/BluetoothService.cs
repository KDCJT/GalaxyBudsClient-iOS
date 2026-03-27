using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExternalAccessory;
using Foundation;
using GalaxyBudsClient.Platform.Interfaces;
using GalaxyBudsClient.Platform.Model;
using Serilog;

namespace GalaxyBudsClient.Platform.iOS;

public class BluetoothService : IBluetoothService
{
    private EAAccessory? _accessory;
    private EASession? _session;
    private Stream? _inputStream;
    private Stream? _outputStream;
    private CancellationTokenSource? _readCts;

    public event EventHandler<BluetoothException>? BluetoothErrorAsync;
    public event EventHandler? Connecting;
    public event EventHandler? Connected;
    public event EventHandler? RfcommConnected;
    public event EventHandler<string>? Disconnected;
    public event EventHandler<byte[]>? NewDataAvailable;

    public bool IsStreamConnected => _session != null && _inputStream != null && _outputStream != null;

    // Known Samsung Galaxy Buds protocol strings
    private static readonly string[] ProtocolStrings = 
    {
        "com.samsung.accessory.galaxybuds",
        "com.samsung.accessory.galaxybuds2",
        "com.samsung.accessory.galaxybudspro",
        "com.samsung.accessory.galaxybuds2pro",
        "com.samsung.accessory.galaxybudslive",
        "com.samsung.accessory.galaxybudsplus"
    };

    public Task<BluetoothDevice[]> GetDevicesAsync()
    {
        var accessories = EAAccessoryManager.SharedAccessoryManager.ConnectedAccessories;
        var devices = accessories
            .Select(a => new BluetoothDevice(
                a.Name,
                a.SerialNumber, // EA doesn't expose MAC address directly, using SerialNumber as ID
                true,
                true,
                new BluetoothCoD(0), // CoD not easily available via EA
                a.ProtocolStrings))
            .ToArray();
        
        return Task.FromResult(devices);
    }

    public async Task ConnectAsync(string macAddress, string serviceUuid, CancellationToken cancelToken)
    {
        try
        {
            Connecting?.Invoke(this, EventArgs.Empty);

            // In EA, we find by SerialNumber or name since MAC is hidden
            _accessory = EAAccessoryManager.SharedAccessoryManager.ConnectedAccessories
                .FirstOrDefault(a => a.SerialNumber == macAddress || a.Name.Contains("Buds"));

            if (_accessory == null)
            {
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed, "未找到目标耳机，请确保已在系统蓝牙中配对。");
            }

            // Find matching protocol string
            string? protocol = _accessory.ProtocolStrings.Intersect(ProtocolStrings).FirstOrDefault();
            if (protocol == null)
            {
                // Fallback to first if none match, or use default
                protocol = _accessory.ProtocolStrings.FirstOrDefault() ?? ProtocolStrings[0];
                Log.Warning("iOS.BluetoothService: No known protocol string found. Attempting with: {Protocol}", protocol);
            }

            _session = new EASession(_accessory, protocol);
            if (_session == null)
            {
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed, "无法建立 EASession。");
            }

            _inputStream = _session.InputStream.AsStream();
            _outputStream = _session.OutputStream.AsStream();

            _session.InputStream.Schedule(NSRunLoop.Main, NSRunLoop.NSDefaultRunLoopMode);
            _session.OutputStream.Schedule(NSRunLoop.Main, NSRunLoop.NSDefaultRunLoopMode);

            _session.InputStream.Open();
            _session.OutputStream.Open();

            Connected?.Invoke(this, EventArgs.Empty);
            RfcommConnected?.Invoke(this, EventArgs.Empty);

            _readCts = new CancellationTokenSource();
            _ = Task.Run(() => ReadLoop(_readCts.Token), _readCts.Token);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "iOS.BluetoothService: Connection failed");
            BluetoothErrorAsync?.Invoke(this, new BluetoothException(BluetoothException.ErrorCodes.Unknown, ex.Message));
            throw;
        }
    }

    private async Task ReadLoop(CancellationToken token)
    {
        var buffer = new byte[1024];
        try
        {
            while (!token.IsCancellationRequested && _inputStream != null)
            {
                int bytesRead = await _inputStream.ReadAsync(buffer, 0, buffer.Length, token);
                if (bytesRead > 0)
                {
                    var data = new byte[bytesRead];
                    Array.Copy(buffer, 0, data, 0, bytesRead);
                    NewDataAvailable?.Invoke(this, data);
                }
                else if (bytesRead == 0)
                {
                    break; // EOF
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("iOS.BluetoothService: Read loop terminated: {Msg}", ex.Message);
        }
        finally
        {
            await DisconnectAsync();
        }
    }

    public Task DisconnectAsync()
    {
        _readCts?.Cancel();
        
        _inputStream?.Close();
        _outputStream?.Close();
        
        _session?.InputStream.Close();
        _session?.OutputStream.Close();
        _session?.Dispose();
        
        _session = null;
        _inputStream = null;
        _outputStream = null;

        Disconnected?.Invoke(this, "已断开连接");
        return Task.CompletedTask;
    }

    public async Task SendAsync(byte[] data)
    {
        if (_outputStream != null)
        {
            await _outputStream.WriteAsync(data, 0, data.Length);
            await _outputStream.FlushAsync();
        }
    }
}
