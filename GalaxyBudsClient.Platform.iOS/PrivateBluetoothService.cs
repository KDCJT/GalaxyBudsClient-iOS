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

public class PrivateBluetoothService : IBluetoothService
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Logs", "bluetooth.log");

    private nint _btManager = nint.Zero;
    private nint _connectedDevice = nint.Zero;

    public event EventHandler<BluetoothException>? BluetoothErrorAsync;
    public event EventHandler? Connecting;
    public event EventHandler? Connected;
    public event EventHandler? RfcommConnected;
    public event EventHandler<string>? Disconnected;
    public event EventHandler<byte[]>? NewDataAvailable;

    public bool IsStreamConnected { get; private set; }

    // --- P/Invoke: objc_msgSend overloads ---
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

    // --- P/Invoke: ObjC runtime introspection ---
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "class_copyMethodList")]
    private static extern nint ClassCopyMethodList(nint cls, out uint outCount);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "method_getName")]
    private static extern nint MethodGetName(nint method);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_getName")]
    private static extern nint SelGetNamePtr(nint selector);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlopen")]
    private static extern nint DlOpen(string path, int mode);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "free")]
    private static extern void Free(nint ptr);

    public PrivateBluetoothService()
    {
        InitBluetoothManager();
    }

    private void InitBluetoothManager()
    {
        try
        {
            Log("Initializing PrivateBluetoothService...");
            DlOpen("/System/Library/PrivateFrameworks/BluetoothManager.framework/BluetoothManager", 1);

            var btClass = Class.GetHandle("BluetoothManager");
            if (btClass == nint.Zero) { Log("ERROR: BluetoothManager class not found"); return; }

            _btManager = MsgSend(btClass, Selector.GetHandle("sharedInstance"));
            Log($"BluetoothManager sharedInstance: 0x{_btManager:X}");

            if (_btManager == nint.Zero) { Log("ERROR: sharedInstance nil"); return; }

            // Dump ALL instance methods so we know exactly what's available on this iOS version
            DumpAllMethods(btClass, "BluetoothManager");

            // Also try BTLocalDevice which is the newer API on iOS 15
            var btLocalClass = Class.GetHandle("BTLocalDevice");
            if (btLocalClass != nint.Zero)
            {
                Log("BTLocalDevice class found, dumping its methods too...");
                DumpAllMethods(btLocalClass, "BTLocalDevice");
                var localDev = MsgSend(btLocalClass, Selector.GetHandle("sharedInstance"));
                Log($"BTLocalDevice sharedInstance: 0x{localDev:X}");
                if (localDev != nint.Zero)
                {
                    // Try pairedDevices on BTLocalDevice
                    TryListDevices(localDev, "BTLocalDevice");
                }
            }
            else
            {
                Log("BTLocalDevice class NOT found.");
            }

            // Try all known device-list selectors on BluetoothManager
            TryListDevices(_btManager, "BluetoothManager");
        }
        catch (Exception ex) { Log($"InitBluetoothManager ERROR: {ex}"); }
    }

    private void DumpAllMethods(nint classHandle, string className)
    {
        try
        {
            var methodsPtr = ClassCopyMethodList(classHandle, out uint count);
            Log($"=== {className}: {count} instance methods ===");
            for (uint i = 0; i < count; i++)
            {
                var methodPtr = Marshal.ReadIntPtr((IntPtr)(methodsPtr + (nint)(i * IntPtr.Size)));
                var selPtr = MethodGetName(methodPtr);
                var namePtr = SelGetNamePtr(selPtr);
                var name = Marshal.PtrToStringAnsi((IntPtr)namePtr) ?? "?";
                Log($"  {name}");
            }
            Free(methodsPtr);
        }
        catch (Exception ex) { Log($"DumpAllMethods({className}) ERROR: {ex.Message}"); }
    }

    private void TryListDevices(nint manager, string tag)
    {
        var selectors = new[] { "pairedDevices", "connectedDevices", "devices", "deviceList", "allDevices" };
        foreach (var sel in selectors)
        {
            try
            {
                var ptr = MsgSend(manager, Selector.GetHandle(sel));
                if (ptr == nint.Zero) { Log($"[{tag}] '{sel}' → nil"); continue; }
                var count = MsgSendUint(ptr, Selector.GetHandle("count"));
                Log($"[{tag}] '{sel}' → count={count}");
            }
            catch (Exception ex)
            {
                // Only log the reason, not the full stack trace for known "unrecognized selector" cases
                var reason = ex.Message.Contains("unrecognized selector") 
                    ? "unrecognized selector" : ex.Message;
                Log($"[{tag}] '{sel}' → {reason}");
            }
        }
    }

    public Task<BluetoothDevice[]> GetDevicesAsync()
    {
        if (_btManager == nint.Zero)
            return Task.FromResult(Array.Empty<BluetoothDevice>());

        try
        {
            foreach (var sel in new[] { "pairedDevices", "connectedDevices" })
            {
                try
                {
                    var listPtr = MsgSend(_btManager, Selector.GetHandle(sel));
                    if (listPtr == nint.Zero) continue;
                    var count = MsgSendUint(listPtr, Selector.GetHandle("count"));
                    if (count == 0) continue;
                    Log($"GetDevicesAsync: '{sel}' returned {count} devices");
                    return Task.FromResult(ReadDeviceArray(listPtr, (int)count));
                }
                catch { }
            }

            Log("GetDevicesAsync: no devices found via API.");
            return Task.FromResult(Array.Empty<BluetoothDevice>());
        }
        catch (Exception ex)
        {
            Log($"GetDevicesAsync ERROR: {ex.Message}");
            return Task.FromResult(Array.Empty<BluetoothDevice>());
        }
    }

    private BluetoothDevice[] ReadDeviceArray(nint listPtr, int count)
    {
        var result = new List<BluetoothDevice>();
        for (nuint i = 0; i < (nuint)count; i++)
        {
            try
            {
                var devPtr = MsgSendP(listPtr, Selector.GetHandle("objectAtIndex:"), (nint)i);
                var name = GetNSString(devPtr, "name");
                var addr = GetNSString(devPtr, "address");
                var connected = MsgSendBool(devPtr, Selector.GetHandle("isConnected"));
                Log($"  [{i}] {name} @ {addr} conn={connected}");
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
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed, "BluetoothManager 未初始化");

            nint targetDevice = nint.Zero;

            // Try deviceForAddress: with the user-provided MAC
            try
            {
                using var nsAddr = new NSString(macAddress);
                targetDevice = MsgSendP(_btManager, Selector.GetHandle("deviceForAddress:"), nsAddr.Handle);
                Log($"deviceForAddress: → 0x{targetDevice:X}");
            }
            catch (Exception ex) { Log($"deviceForAddress: error: {ex.Message}"); }

            if (targetDevice == nint.Zero)
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
                    $"找不到设备 {macAddress}。请确认 MAC 地址正确并查看 bluetooth.log 中的方法列表。");

            _connectedDevice = targetDevice;
            MsgSendVoidP(_btManager, Selector.GetHandle("connectDevice:"), targetDevice);
            Log("connectDevice: called");

            await Task.Delay(2500, cancelToken);

            bool isConnected = MsgSendBool(targetDevice, Selector.GetHandle("isConnected"));
            Log($"isConnected={isConnected}");

            if (!isConnected)
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
                    "connectDevice: 后 isConnected 仍为 false");

            IsStreamConnected = true;
            Connected?.Invoke(this, EventArgs.Empty);
            RfcommConnected?.Invoke(this, EventArgs.Empty);
        }
        catch (BluetoothException) { throw; }
        catch (Exception ex)
        {
            Log($"ConnectAsync ERROR: {ex}");
            throw new BluetoothException(BluetoothException.ErrorCodes.Unknown, ex.Message);
        }
    }

    private string GetNSString(nint obj, string sel)
    {
        try
        {
            var ptr = MsgSend(obj, Selector.GetHandle(sel));
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
        catch { }
        IsStreamConnected = false;
        _connectedDevice = nint.Zero;
        Disconnected?.Invoke(this, "Disconnected");
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
        try { File.AppendAllText(LogPath, $"[BT] {DateTime.Now}: {msg}\n"); } catch { }
    }
}
