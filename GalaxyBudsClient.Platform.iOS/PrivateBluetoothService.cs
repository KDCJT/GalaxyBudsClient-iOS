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
/// Bluetooth service using the private iOS BluetoothManager.framework.
/// Uses direct P/Invoke to libobjc.dylib to avoid .NET 10 accessibility issues.
///
/// Key insight: BluetoothManager communicates via XPC with bluetoothd.
/// A registered delegate is required before pairedDevices is populated.
/// As a fallback, we support manual MAC address entry via deviceForAddress:.
/// </summary>
public class PrivateBluetoothService : IBluetoothService
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Logs", "bluetooth.log");

    private nint _btManager = nint.Zero;
    private nint _connectedDevice = nint.Zero;
    private BluetoothManagerDelegate? _managerDelegate;

    public event EventHandler<BluetoothException>? BluetoothErrorAsync;
    public event EventHandler? Connecting;
    public event EventHandler? Connected;
    public event EventHandler? RfcommConnected;
    public event EventHandler<string>? Disconnected;
    public event EventHandler<byte[]>? NewDataAvailable;

    public bool IsStreamConnected { get; private set; }

    // --- P/Invoke to libobjc.dylib ---
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint MsgSend(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint MsgSendP(nint receiver, nint selector, nint arg1);

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
            Log("Initializing PrivateBluetoothService...");
            dlopen("/System/Library/PrivateFrameworks/BluetoothManager.framework/BluetoothManager", 1);

            var btClass = Class.GetHandle("BluetoothManager");
            if (btClass == nint.Zero) { Log("ERROR: BluetoothManager class not found"); return; }

            _btManager = MsgSend(btClass, Selector.GetHandle("sharedInstance"));
            Log($"BluetoothManager sharedInstance: 0x{_btManager:X}");

            if (_btManager == nint.Zero) { Log("ERROR: sharedInstance returned nil"); return; }

            // CRITICAL: Register a delegate before pairedDevices will work.
            // The manager needs to establish XPC connection with bluetoothd.
            _managerDelegate = new BluetoothManagerDelegate(
                onReady: () => Log("BluetoothManager delegate: bluetoothManagerReady fired!"),
                onDeviceFound: (ptr, name, addr) => Log($"Delegate DeviceFound: {name} @ {addr}"),
                onData: (data) => NewDataAvailable?.Invoke(this, data),
                onDisconnect: () => {
                    IsStreamConnected = false;
                    Disconnected?.Invoke(this, "Device disconnected");
                });

            // Register as delegate ([btManager addDelegate:self])
            MsgSendVoidP(_btManager, Selector.GetHandle("addDelegate:"), _managerDelegate.Handle);
            Log("addDelegate: called");

            // Try to enumerate selectors for discovery (multi-try)
            LogPairedDevicesAttempts();
        }
        catch (Exception ex) { Log($"InitBluetoothManager ERROR: {ex}"); }
    }

    private void LogPairedDevicesAttempts()
    {
        var selectors = new[] { "pairedDevices", "connectedDevices", "devices", "deviceList" };
        foreach (var sel in selectors)
        {
            try
            {
                var ptr = MsgSend(_btManager, Selector.GetHandle(sel));
                if (ptr != nint.Zero)
                {
                    var count = MsgSendUint(ptr, Selector.GetHandle("count"));
                    Log($"  Selector '{sel}' → count={count}, ptr=0x{ptr:X}");
                }
                else
                {
                    Log($"  Selector '{sel}' → nil");
                }
            }
            catch (Exception ex) { Log($"  Selector '{sel}' → exception: {ex.Message}"); }
        }
    }

    public Task<BluetoothDevice[]> GetDevicesAsync()
    {
        if (_btManager == nint.Zero)
            return Task.FromResult(Array.Empty<BluetoothDevice>());

        try
        {
            // Try all known selectors
            foreach (var sel in new[] { "pairedDevices", "connectedDevices", "devices" })
            {
                var listPtr = MsgSend(_btManager, Selector.GetHandle(sel));
                if (listPtr == nint.Zero) continue;

                var count = MsgSendUint(listPtr, Selector.GetHandle("count"));
                if (count == 0) continue;

                Log($"GetDevicesAsync: '{sel}' returned {count} devices");
                return Task.FromResult(ReadDeviceArray(listPtr, (int)count));
            }

            Log("GetDevicesAsync: all selectors returned empty. User must enter MAC manually.");
            return Task.FromResult(Array.Empty<BluetoothDevice>());
        }
        catch (Exception ex)
        {
            Log($"GetDevicesAsync ERROR: {ex}");
            return Task.FromResult(Array.Empty<BluetoothDevice>());
        }
    }

    private BluetoothDevice[] ReadDeviceArray(nint listPtr, int count)
    {
        var result = new List<BluetoothDevice>();
        var objAtSel = Selector.GetHandle("objectAtIndex:");
        for (nuint i = 0; i < (nuint)count; i++)
        {
            try
            {
                var devPtr = MsgSendP(listPtr, objAtSel, (nint)i);
                var name = GetNSString(devPtr, "name");
                var addr = GetNSString(devPtr, "address");
                var connected = MsgSendBool(devPtr, Selector.GetHandle("isConnected"));
                Log($"  [{i}] {name} @ {addr} connected={connected}");
                result.Add(new BluetoothDevice(name, addr, true, connected, new BluetoothCoD(0), null));
            }
            catch (Exception ex) { Log($"  [{i}] error: {ex.Message}"); }
        }
        return result.ToArray();
    }

    public async Task ConnectAsync(string macAddress, string serviceUuid, CancellationToken cancelToken)
    {
        try
        {
            Log($"ConnectAsync: MAC={macAddress}");
            Connecting?.Invoke(this, EventArgs.Empty);

            if (_btManager == nint.Zero)
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
                    "BluetoothManager 未初始化");

            // Try to get device: first from pairedDevices list, then via deviceForAddress:
            nint targetDevice = FindDeviceByAddress(macAddress);

            if (targetDevice == nint.Zero)
            {
                Log("Device not in paired list, trying deviceForAddress:");
                using var nsAddr = new NSString(macAddress);
                targetDevice = MsgSendP(_btManager,
                    Selector.GetHandle("deviceForAddress:"), nsAddr.Handle);
                Log($"deviceForAddress: returned 0x{targetDevice:X}");
            }

            if (targetDevice == nint.Zero)
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
                    $"找不到设备 {macAddress}，请确认 MAC 地址正确且耳机已在 iOS 蓝牙设置中配对");

            _connectedDevice = targetDevice;
            Log("Calling connectDevice:");
            MsgSendVoidP(_btManager, Selector.GetHandle("connectDevice:"), targetDevice);

            await Task.Delay(2500, cancelToken);

            bool isNowConnected = MsgSendBool(targetDevice, Selector.GetHandle("isConnected"));
            Log($"isConnected after connect: {isNowConnected}");

            if (!isNowConnected)
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
                    "connectDevice: 后 isConnected 仍为 false");

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

    private nint FindDeviceByAddress(string mac)
    {
        foreach (var sel in new[] { "pairedDevices", "connectedDevices", "devices" })
        {
            try
            {
                var listPtr = MsgSend(_btManager, Selector.GetHandle(sel));
                if (listPtr == nint.Zero) continue;
                var count = MsgSendUint(listPtr, Selector.GetHandle("count"));
                for (nuint i = 0; i < count; i++)
                {
                    var devPtr = MsgSendP(listPtr, Selector.GetHandle("objectAtIndex:"), (nint)i);
                    var addr = GetNSString(devPtr, "address");
                    if (string.Equals(addr, mac, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(addr.Replace(":", "-"), mac.Replace(":", "-"),
                            StringComparison.OrdinalIgnoreCase))
                        return devPtr;
                }
            }
            catch { }
        }
        return nint.Zero;
    }

    private void TryOpenRfcommChannel(nint devicePtr)
    {
        var delegateHandle = (_managerDelegate as NSObject)!.Handle;
        // Try multiple known selector names for iOS 15 RFCOMM
        var selectors = new[] {
            ("openRFCOMMChannelAsync:delegate:", true),
            ("openRFCOMMChannel:delegate:", true),
            ("connectRFCOMM:", false)
        };

        foreach (var (sel, hasDelegate) in selectors)
        {
            try
            {
                Log($"Trying RFCOMM selector: {sel}");
                if (hasDelegate)
                    MsgSendVoidPP(devicePtr, Selector.GetHandle(sel), (nint)1, delegateHandle);
                else
                    MsgSendVoidP(devicePtr, Selector.GetHandle(sel), (nint)1);
                Log($"RFCOMM selector {sel} succeeded.");
                return;
            }
            catch (Exception ex) { Log($"RFCOMM selector {sel} failed: {ex.Message}"); }
        }
    }

    private string GetNSString(nint obj, string selectorName)
    {
        try
        {
            var ptr = MsgSend(obj, Selector.GetHandle(selectorName));
            return ptr != nint.Zero ? Runtime.GetNSObject<NSString>(ptr)?.ToString() ?? "" : "";
        }
        catch { return ""; }
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
/// NSObject delegate for BluetoothManager private callbacks.
/// </summary>
[Register("BluetoothManagerDelegate")]
internal class BluetoothManagerDelegate : NSObject
{
    private readonly Action _onReady;
    private readonly Action<nint, string, string> _onDeviceFound;
    private readonly Action<byte[]> _onData;
    private readonly Action _onDisconnect;

    public BluetoothManagerDelegate(Action onReady,
        Action<nint, string, string> onDeviceFound,
        Action<byte[]> onData,
        Action onDisconnect)
    {
        _onReady = onReady;
        _onDeviceFound = onDeviceFound;
        _onData = onData;
        _onDisconnect = onDisconnect;
    }

    // Called when BluetoothManager is ready (XPC connection established with bluetoothd)
    [Export("bluetoothManagerReady")]
    public void BluetoothManagerReady() => _onReady();

    [Export("bluetoothAvailabilityChanged:")]
    public void BluetoothAvailabilityChanged(bool available)
    {
        File.AppendAllText(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Logs", "bluetooth.log"),
            $"[BT-Private] {DateTime.Now}: bluetoothAvailabilityChanged: {available}\n");
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
    public void ClosedChannel(nint device, nint channel) => _onDisconnect();
}
