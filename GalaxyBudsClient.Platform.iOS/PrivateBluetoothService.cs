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
/// Bluetooth service using the private iOS BluetoothManager.framework via direct P/Invoke.
/// ObjCRuntime.Messaging is internal in .NET 10, so we call libobjc.dylib directly.
/// </summary>
public class PrivateBluetoothService : IBluetoothService
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Logs", "bluetooth.log");

    private nint _btManager = nint.Zero;
    private nint _connectedDevice = nint.Zero;
    private BluetoothDataDelegate? _dataDelegate;

    public event EventHandler<BluetoothException>? BluetoothErrorAsync;
    public event EventHandler? Connecting;
    public event EventHandler? Connected;
    public event EventHandler? RfcommConnected;
    public event EventHandler<string>? Disconnected;
    public event EventHandler<byte[]>? NewDataAvailable;

    public bool IsStreamConnected { get; private set; }

    // --- P/Invoke into libobjc.dylib (available on all iOS versions) ---
    // We use overloads because each signature must be declared separately for P/Invoke

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint MsgSend(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint MsgSendP(nint receiver, nint selector, nint arg1);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoid(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoidP(nint receiver, nint selector, nint arg1);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoidPP(nint receiver, nint selector, nint arg1, nint arg2);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MsgSendBool(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nuint MsgSendUint(nint receiver, nint selector);

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern nint dlopen(string path, int mode);

    public PrivateBluetoothService()
    {
        InitBluetoothManager();
    }

    private void InitBluetoothManager()
    {
        try
        {
            Log("Initializing PrivateBluetoothService via libobjc P/Invoke...");

            var handle = dlopen(
                "/System/Library/PrivateFrameworks/BluetoothManager.framework/BluetoothManager", 1);
            Log($"BluetoothManager.framework dlopen: 0x{handle:X}");

            var btClass = Class.GetHandle("BluetoothManager");
            Log($"BluetoothManager class handle: 0x{btClass:X}");

            if (btClass == nint.Zero)
            {
                Log("ERROR: BluetoothManager class not found");
                return;
            }

            _btManager = MsgSend(btClass, Selector.GetHandle("sharedInstance"));
            Log($"BluetoothManager sharedInstance: 0x{_btManager:X}");
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
                Log("GetDevicesAsync: btManager not initialized");
                return Task.FromResult(Array.Empty<BluetoothDevice>());
            }

            // Try pairedDevices first, then connectedDevices as fallback
            nint listPtr = MsgSend(_btManager, Selector.GetHandle("pairedDevices"));
            Log($"pairedDevices ptr: 0x{listPtr:X}");

            if (listPtr == nint.Zero)
            {
                listPtr = MsgSend(_btManager, Selector.GetHandle("connectedDevices"));
                Log($"connectedDevices fallback ptr: 0x{listPtr:X}");
            }

            if (listPtr == nint.Zero)
            {
                Log("Both pairedDevices and connectedDevices returned nil.");
                return Task.FromResult(Array.Empty<BluetoothDevice>());
            }

            // Get NSArray count
            nuint count = MsgSendUint(listPtr, Selector.GetHandle("count"));
            Log($"Device list count: {count}");

            var nameSel = Selector.GetHandle("name");
            var addrSel = Selector.GetHandle("address");
            var isConnSel = Selector.GetHandle("isConnected");
            var objectAtSel = Selector.GetHandle("objectAtIndex:");

            var result = new List<BluetoothDevice>();
            for (nuint i = 0; i < count; i++)
            {
                try
                {
                    nint devPtr = MsgSendP(listPtr, objectAtSel, (nint)i);

                    nint namePtr = MsgSend(devPtr, nameSel);
                    string name = namePtr != nint.Zero
                        ? Runtime.GetNSObject<NSString>(namePtr)?.ToString() ?? "Unknown"
                        : "Unknown";

                    nint addrPtr = MsgSend(devPtr, addrSel);
                    string address = addrPtr != nint.Zero
                        ? Runtime.GetNSObject<NSString>(addrPtr)?.ToString() ?? "Unknown"
                        : "Unknown";

                    bool isConnected = MsgSendBool(devPtr, isConnSel);

                    Log($"  [{i}] Name={name}, Addr={address}, Connected={isConnected}");
                    result.Add(new BluetoothDevice(name, address, true, isConnected, new BluetoothCoD(0), null));
                }
                catch (Exception ex)
                {
                    Log($"  [{i}] device read error: {ex.Message}");
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
            Log($"ConnectAsync: target MAC={macAddress}");
            Connecting?.Invoke(this, EventArgs.Empty);

            if (_btManager == nint.Zero)
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
                    "BluetoothManager 私有框架未能初始化");

            // Find device by address
            nint listPtr = MsgSend(_btManager, Selector.GetHandle("pairedDevices"));
            if (listPtr == nint.Zero)
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
                    "pairedDevices 返回 nil");

            nuint count = MsgSendUint(listPtr, Selector.GetHandle("count"));
            nint targetDevice = nint.Zero;

            for (nuint i = 0; i < count; i++)
            {
                nint devPtr = MsgSendP(listPtr, Selector.GetHandle("objectAtIndex:"), (nint)i);
                nint addrPtr = MsgSend(devPtr, Selector.GetHandle("address"));
                string addr = addrPtr != nint.Zero
                    ? Runtime.GetNSObject<NSString>(addrPtr)?.ToString() ?? ""
                    : "";

                if (string.Equals(addr.Replace(":", "-"), macAddress.Replace(":", "-"),
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(addr, macAddress, StringComparison.OrdinalIgnoreCase))
                {
                    targetDevice = devPtr;
                    Log($"Found target device at index {i}");
                    break;
                }
            }

            if (targetDevice == nint.Zero)
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
                    $"未找到 MAC={macAddress} 的配对设备");

            _connectedDevice = targetDevice;

            // Setup data delegate
            _dataDelegate = new BluetoothDataDelegate(OnDataReceived, OnDeviceDisconnected);

            // Call connectDevice: to initiate connection
            Log("Calling connectDevice:");
            MsgSendVoidP(_btManager, Selector.GetHandle("connectDevice:"), targetDevice);

            await Task.Delay(2000, cancelToken);

            bool isNowConnected = MsgSendBool(targetDevice, Selector.GetHandle("isConnected"));
            Log($"isConnected after connect: {isNowConnected}");

            if (!isNowConnected)
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
                    "connectDevice: 调用后 isConnected 仍为 false");

            IsStreamConnected = true;
            Connected?.Invoke(this, EventArgs.Empty);
            RfcommConnected?.Invoke(this, EventArgs.Empty);

            TryOpenRfcommChannel(targetDevice);
        }
        catch (BluetoothException) { throw; }
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
            Log("Opening RFCOMM channel (channel 1)...");
            // Try primary selector for iOS 15
            MsgSendVoidPP(devicePtr,
                Selector.GetHandle("openRFCOMMChannelAsync:delegate:"),
                (nint)1,
                (_dataDelegate as NSObject)!.Handle);
            Log("openRFCOMMChannelAsync:delegate: called.");
        }
        catch (Exception ex)
        {
            Log($"openRFCOMMChannelAsync failed: {ex.Message}, trying connectRFCOMM:");
            try
            {
                MsgSendVoidP(devicePtr, Selector.GetHandle("connectRFCOMM:"), (nint)1);
                Log("connectRFCOMM: called.");
            }
            catch (Exception ex2)
            {
                Log($"All RFCOMM open attempts failed: {ex2.Message}");
            }
        }
    }

    private void OnDataReceived(byte[] data)
    {
        NewDataAvailable?.Invoke(this, data);
    }

    private void OnDeviceDisconnected()
    {
        IsStreamConnected = false;
        Disconnected?.Invoke(this, "Device disconnected");
    }

    public Task DisconnectAsync()
    {
        try
        {
            if (_connectedDevice != nint.Zero && _btManager != nint.Zero)
                MsgSendVoidP(_btManager, Selector.GetHandle("disconnectDevice:"), _connectedDevice);
        }
        catch (Exception ex) { Log($"DisconnectAsync ERROR: {ex.Message}"); }

        IsStreamConnected = false;
        _connectedDevice = nint.Zero;
        Disconnected?.Invoke(this, "Disconnected by user");
        return Task.CompletedTask;
    }

    public Task SendAsync(byte[] data)
    {
        try
        {
            if (!IsStreamConnected || _connectedDevice == nint.Zero) return Task.CompletedTask;
            using var nsData = NSData.FromArray(data);
            MsgSendVoidP(_connectedDevice, Selector.GetHandle("sendData:"), nsData.Handle);
        }
        catch (Exception ex) { Log($"SendAsync ERROR: {ex.Message}"); }
        return Task.CompletedTask;
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"[BT-Private] {DateTime.Now}: {msg}\n"); } catch { }
    }
}

/// <summary>
/// NSObject delegate that receives callbacks from the private BluetoothManager.framework.
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
