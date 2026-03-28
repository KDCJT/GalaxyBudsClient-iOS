using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Foundation;
using GalaxyBudsClient.Platform.Interfaces;
using GalaxyBudsClient.Platform.Model;
using ObjCRuntime;

namespace GalaxyBudsClient.Platform.iOS;

/// <summary>
/// Bluetooth service implementation using iOS private BluetoothManager.framework.
/// This approach bypasses the MFi certification requirement of EAAccessory,
/// allowing connection to Galaxy Buds Pro via RFCOMM/SPP using TrollStore entitlements.
/// 
/// Requires entitlements:
///   com.apple.private.bluetooth = true
/// </summary>
public class PrivateBluetoothService : IBluetoothService
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Logs", "bluetooth.log");

    private nint _btManager = nint.Zero;
    private nint _connectedDevice = nint.Zero;
    private BluetoothDataDelegate? _dataDelegate;
    private bool _frameworkLoaded;

    public event EventHandler<BluetoothException>? BluetoothErrorAsync;
    public event EventHandler? Connecting;
    public event EventHandler? Connected;
    public event EventHandler? RfcommConnected;
    public event EventHandler<string>? Disconnected;
    public event EventHandler<byte[]>? NewDataAvailable;

    public bool IsStreamConnected { get; private set; }

    // dlopen the private framework to make its classes available
    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern nint dlopen(string path, int mode);

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern nint dlerror();

    public PrivateBluetoothService()
    {
        InitBluetoothManager();
    }

    private void InitBluetoothManager()
    {
        try
        {
            Log("Initializing PrivateBluetoothService...");

            // Load the private BluetoothManager framework
            var handle = dlopen(
                "/System/Library/PrivateFrameworks/BluetoothManager.framework/BluetoothManager", 1);
            _frameworkLoaded = handle != nint.Zero;
            Log($"BluetoothManager.framework dlopen result: {handle} (loaded={_frameworkLoaded})");

            var btClass = Class.GetHandle("BluetoothManager");
            if (btClass == nint.Zero)
            {
                Log("ERROR: BluetoothManager class handle is zero - framework not available");
                return;
            }

            _btManager = Messaging.IntPtr_objc_msgSend(btClass, Selector.GetHandle("sharedInstance"));
            Log($"BluetoothManager sharedInstance: 0x{_btManager:X}");

            if (_btManager == nint.Zero)
            {
                Log("ERROR: BluetoothManager sharedInstance returned null");
            }
            else
            {
                Log("PrivateBluetoothService initialized successfully.");
            }
        }
        catch (Exception ex)
        {
            Log($"InitBluetoothManager ERROR: {ex}");
        }
    }

    public Task<BluetoothDevice[]> GetDevicesAsync()
    {
        try
        {
            if (_btManager == nint.Zero)
            {
                Log("GetDevicesAsync: btManager not initialized, falling back");
                return Task.FromResult(Array.Empty<BluetoothDevice>());
            }

            // [BluetoothManager sharedInstance].pairedDevices
            var pairedPtr = Messaging.IntPtr_objc_msgSend(_btManager,
                Selector.GetHandle("pairedDevices"));

            if (pairedPtr == nint.Zero)
            {
                Log("GetDevicesAsync: pairedDevices returned nil. Trying connectedDevices...");
                pairedPtr = Messaging.IntPtr_objc_msgSend(_btManager,
                    Selector.GetHandle("connectedDevices"));
            }

            if (pairedPtr == nint.Zero)
            {
                Log("GetDevicesAsync: Both pairedDevices and connectedDevices returned nil.");
                return Task.FromResult(Array.Empty<BluetoothDevice>());
            }

            var devicesArray = Runtime.GetNSObject<NSArray>(pairedPtr)!;
            Log($"GetDevicesAsync: Found {devicesArray.Count} devices in pairedDevices");

            var result = new List<BluetoothDevice>();
            for (nuint i = 0; i < devicesArray.Count; i++)
            {
                try
                {
                    var devPtr = devicesArray.ValueAt(i);

                    var namePtr = Messaging.IntPtr_objc_msgSend(devPtr, Selector.GetHandle("name"));
                    string name = Runtime.GetNSObject<NSString>(namePtr)?.ToString() ?? "Unknown";

                    var addrPtr = Messaging.IntPtr_objc_msgSend(devPtr, Selector.GetHandle("address"));
                    string address = Runtime.GetNSObject<NSString>(addrPtr)?.ToString() ?? "Unknown";

                    bool isConnected = Messaging.bool_objc_msgSend(devPtr, Selector.GetHandle("isConnected"));

                    Log($"  [{i}] Name={name}, Address={address}, Connected={isConnected}");
                    result.Add(new BluetoothDevice(name, address, true, isConnected, new BluetoothCoD(0), null));
                }
                catch (Exception devEx)
                {
                    Log($"  [{i}] Error reading device: {devEx.Message}");
                }
            }

            return Task.FromResult(result.ToArray());
        }
        catch (Exception ex)
        {
            Log($"GetDevicesAsync ERROR: {ex}");
            return Task.FromResult(Array.Empty<BluetoothDevice>());
        }
    }

    public async Task ConnectAsync(string macAddress, string serviceUuid, CancellationToken cancelToken)
    {
        try
        {
            Log($"ConnectAsync: macAddress={macAddress}");
            Connecting?.Invoke(this, EventArgs.Empty);

            if (_btManager == nint.Zero)
            {
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
                    "BluetoothManager 未初始化（私有框架不可用）");
            }

            // Find the device in pairedDevices by MAC address
            var pairedPtr = Messaging.IntPtr_objc_msgSend(_btManager, Selector.GetHandle("pairedDevices"));
            if (pairedPtr == nint.Zero)
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed, "无法获取配对设备列表");

            var devicesArray = Runtime.GetNSObject<NSArray>(pairedPtr)!;
            nint targetDevice = nint.Zero;

            for (nuint i = 0; i < devicesArray.Count; i++)
            {
                var devPtr = devicesArray.ValueAt(i);
                var addrPtr = Messaging.IntPtr_objc_msgSend(devPtr, Selector.GetHandle("address"));
                string addr = Runtime.GetNSObject<NSString>(addrPtr)?.ToString() ?? "";

                if (string.Equals(addr, macAddress, StringComparison.OrdinalIgnoreCase))
                {
                    targetDevice = devPtr;
                    Log($"ConnectAsync: found target device at index {i}");
                    break;
                }
            }

            if (targetDevice == nint.Zero)
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
                    $"未找到 MAC 地址为 {macAddress} 的配对设备");

            _connectedDevice = targetDevice;

            // Set up data delegate
            _dataDelegate = new BluetoothDataDelegate(OnDataReceived, OnDisconnected);

            // Attempt RFCOMM connection via private API
            // On iOS 15, the method is: connectDevice: (triggers the BT profile connection)
            Log("ConnectAsync: calling connectDevice:");
            Messaging.void_objc_msgSend_IntPtr(_btManager,
                Selector.GetHandle("connectDevice:"), targetDevice);

            // Wait briefly for connection
            await Task.Delay(1500, cancelToken);

            bool isNowConnected = Messaging.bool_objc_msgSend(targetDevice, Selector.GetHandle("isConnected"));
            Log($"ConnectAsync: isConnected after connect call: {isNowConnected}");

            if (isNowConnected)
            {
                IsStreamConnected = true;
                Connected?.Invoke(this, EventArgs.Empty);
                RfcommConnected?.Invoke(this, EventArgs.Empty);

                // Try to open RFCOMM data channel (channel ID 1 for SPP)
                TryOpenRfcommChannel(targetDevice);
            }
            else
            {
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
                    "设备连接后 isConnected 仍为 false");
            }
        }
        catch (BluetoothException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"ConnectAsync ERROR: {ex}");
            throw new BluetoothException(BluetoothException.ErrorCodes.Unknown, ex.Message);
        }
    }

    private void TryOpenRfcommChannel(nint devicePtr)
    {
        try
        {
            Log("TryOpenRfcommChannel: attempting to open RFCOMM channel...");
            // Try opening the RFCOMM channel - channel 0 means "find via SDP"
            // Selector may vary by iOS version; trying common ones
            Messaging.void_objc_msgSend_IntPtr_IntPtr(devicePtr,
                Selector.GetHandle("openRFCOMMChannel:withTarget:"),
                (nint)1,
                (_dataDelegate as NSObject)!.Handle);

            Log("TryOpenRfcommChannel: openRFCOMMChannel called.");
        }
        catch (Exception ex)
        {
            Log($"TryOpenRfcommChannel: selector failed, trying alternative: {ex.Message}");
            try
            {
                Messaging.void_objc_msgSend_IntPtr(devicePtr,
                    Selector.GetHandle("connectRFCOMM:"),
                    (nint)1);
            }
            catch (Exception ex2)
            {
                Log($"TryOpenRfcommChannel: all attempts failed: {ex2.Message}");
            }
        }
    }

    private void OnDataReceived(byte[] data)
    {
        NewDataAvailable?.Invoke(this, data);
    }

    private void OnDisconnected()
    {
        IsStreamConnected = false;
        Disconnected?.Invoke(this, "Device disconnected");
    }

    public Task DisconnectAsync()
    {
        try
        {
            if (_connectedDevice != nint.Zero && _btManager != nint.Zero)
            {
                Messaging.void_objc_msgSend_IntPtr(_btManager,
                    Selector.GetHandle("disconnectDevice:"), _connectedDevice);
            }
        }
        catch (Exception ex)
        {
            Log($"DisconnectAsync ERROR: {ex.Message}");
        }

        IsStreamConnected = false;
        _connectedDevice = nint.Zero;
        Disconnected?.Invoke(this, "Disconnected by user");
        return Task.CompletedTask;
    }

    public Task SendAsync(byte[] data)
    {
        try
        {
            if (!IsStreamConnected || _connectedDevice == nint.Zero)
                return Task.CompletedTask;

            // Write data via RFCOMM channel
            // We use NSData to pass bytes to the ObjC layer
            using var nsData = NSData.FromArray(data);
            Messaging.void_objc_msgSend_IntPtr(_connectedDevice,
                Selector.GetHandle("sendData:"), nsData.Handle);

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Log($"SendAsync ERROR: {ex.Message}");
            return Task.CompletedTask;
        }
    }

    private static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"[BT-Private] {DateTime.Now}: {message}\n"); } catch { }
    }
}

/// <summary>
/// NSObject subclass used as a delegate for receiving Bluetooth data callbacks.
/// The callback selectors are called by the private BluetoothManager.framework.
/// </summary>
[Register("BluetoothDataDelegate")]
internal class BluetoothDataDelegate : NSObject
{
    private readonly Action<byte[]> _onData;
    private readonly Action _onDisconnect;

    public BluetoothDataDelegate(Action<byte[]> onData, Action onDisconnect)
    {
        _onData = onData;
        _onDisconnect = onDisconnect;
    }

    [Export("bluetoothDevice:rfcommChannelData:length:")]
    public void RfcommChannelData(nint device, nint dataPtr, nint length)
    {
        try
        {
            if (dataPtr == nint.Zero || length <= 0) return;
            var data = new byte[(int)length];
            Marshal.Copy(dataPtr, data, 0, (int)length);
            _onData(data);
        }
        catch { }
    }

    [Export("bluetoothDevice:closedChannel:")]
    public void ClosedChannel(nint device, nint channel)
    {
        _onDisconnect();
    }
}
