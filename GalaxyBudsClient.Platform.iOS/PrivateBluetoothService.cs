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

    /// <summary>NSUserDefaults key for the saved Galaxy Buds MAC address.</summary>
    public const string MacAddressDefaultsKey = "GalaxyBudsMacAddress";

    /// <summary>
    /// Callback registered by AppDelegate to show the native MAC input dialog.
    /// Set during AppDelegate initialization to avoid circular project dependencies.
    /// </summary>
    public static Action? ShowMacInputDialog;

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

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoidI(nint receiver, nint selector, int arg1);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlopen")]
    private static extern nint DlOpen(string path, int mode);

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

            // Try _attach to connect XPC and wait briefly for async state update
            try { MsgSendVoidP(_btManager, Selector.GetHandle("_attach"), nint.Zero); } catch { }

            // Read current state (try 3 times with delay for async XPC connection)
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) System.Threading.Thread.Sleep(500);
                try
                {
                    bool en = MsgSendBool(_btManager, Selector.GetHandle("enabled"));
                    bool pw = MsgSendBool(_btManager, Selector.GetHandle("powered"));
                    bool av = MsgSendBool(_btManager, Selector.GetHandle("available"));
                    Log($"State [{attempt}]: enabled={en}, powered={pw}, available={av}");
                    if (en || pw || av) break; // got non-zero state
                }
                catch (Exception ex) { Log($"State check error: {ex.Message}"); }
            }

            // Try to enable scanning
            try
            {
                MsgSendVoidP(_btManager, Selector.GetHandle("setDeviceScanningEnabled:"), (nint)1);
                Log("setDeviceScanningEnabled:YES called");
            }
            catch (Exception ex) { Log($"setDeviceScanningEnabled: error: {ex.Message}"); }
        }
        catch (Exception ex) { Log($"InitBluetoothManager ERROR: {ex}"); }
    }

    public Task<BluetoothDevice[]> GetDevicesAsync()
    {
        try
        {
            // === Strategy 1: BluetoothManager private API ===
            if (_btManager != nint.Zero)
            {
                foreach (var sel in new[] { "pairedDevices", "connectedDevices", "connectingDevices" })
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
            }

            // === Strategy 2: Read iOS Bluetooth pairing database ===
            var fromDb = ReadBluetoothPairingDatabase();
            if (fromDb.Length > 0)
            {
                Log($"GetDevicesAsync: found {fromDb.Length} devices from BT plist database");
                return Task.FromResult(fromDb);
            }

            // === Strategy 3: NSUserDefaults saved MAC ===
            var savedMac = NSUserDefaults.StandardUserDefaults.StringForKey(MacAddressDefaultsKey);
            if (!string.IsNullOrWhiteSpace(savedMac))
            {
                Log($"GetDevicesAsync: returning saved MAC={savedMac}");
                return Task.FromResult(new[]
                {
                    new BluetoothDevice("Galaxy Buds (已保存)", savedMac, true, false, new BluetoothCoD(0), null)
                });
            }

            // === Strategy 4: Trigger scan and show input dialog ===
            TriggerScan();
            Log("GetDevicesAsync: no devices found. Triggering dialog.");
            NSRunLoop.Main.BeginInvokeOnMainThread(() =>
            {
                try { ShowMacInputDialog?.Invoke(); } catch { }
            });

            return Task.FromResult(Array.Empty<BluetoothDevice>());
        }
        catch (Exception ex)
        {
            Log($"GetDevicesAsync ERROR: {ex.Message}");
            return Task.FromResult(Array.Empty<BluetoothDevice>());
        }
    }

    /// <summary>
    /// Reads the iOS Bluetooth pairing database plist to extract MAC addresses and device names.
    /// TrollStore (platform-application) has elevated file system access that allows reading these files.
    /// </summary>
    private BluetoothDevice[] ReadBluetoothPairingDatabase()
    {
        // Known paths for the BT pairing database on iOS 14/15
        var candidatePaths = new[]
        {
            "/private/var/preferences/com.apple.MobileBluetooth.devices.plist",
            "/var/preferences/com.apple.MobileBluetooth.devices.plist",
            "/private/var/mobile/Library/Bluetooth/com.apple.MobileBluetooth.devices.plist",
            "/var/mobile/Library/Bluetooth/com.apple.MobileBluetooth.devices.plist",
            "/private/var/mobile/Library/Preferences/com.apple.MobileBluetooth.plist",
        };

        foreach (var path in candidatePaths)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Log($"BT plist not found: {path}");
                    continue;
                }

                Log($"Reading BT plist: {path}");
                var plistData = NSData.FromFile(path);
                if (plistData == null) { Log($"NSData.FromFile returned nil for {path}"); continue; }

                NSError? err;
                var dict = (NSDictionary?)NSPropertyListSerialization.PropertyListWithData(
                    plistData, NSPropertyListReadOptions.Immutable, out _, out err);

                if (dict == null)
                {
                    Log($"Failed to parse plist at {path}: {err?.LocalizedDescription}");
                    continue;
                }

                Log($"Parsed plist: {dict.Count} top-level keys");
                var devices = new List<BluetoothDevice>();

                foreach (var key in dict.Keys)
                {
                    var macAddr = key.ToString() ?? "";
                    if (!macAddr.Contains(":") && !macAddr.Contains("-")) continue; // not a MAC address key

                    var deviceDict = dict.ObjectForKey(key) as NSDictionary;
                    if (deviceDict == null) continue;

                    // Try to get device name from common plist keys
                    var name = (deviceDict["Name"] as NSString)?.ToString()
                               ?? (deviceDict["DefaultName"] as NSString)?.ToString()
                               ?? (deviceDict["ProductName"] as NSString)?.ToString()
                               ?? "Unknown";

                    Log($"  BT DB device: {name} @ {macAddr}");

                    // Only include if it looks like a Samsung/Galaxy Buds device
                    // (but also return all for diagnostic purposes on first run)
                    devices.Add(new BluetoothDevice(name, macAddr, true, false, new BluetoothCoD(0), null));
                }

                if (devices.Count > 0)
                {
                    // Save the first Galaxy Buds device found to NSUserDefaults
                    var buds = devices.FirstOrDefault(d =>
                        d.Name.Contains("Buds", StringComparison.OrdinalIgnoreCase) ||
                        d.Name.Contains("Galaxy", StringComparison.OrdinalIgnoreCase) ||
                        d.Name.Contains("Samsung", StringComparison.OrdinalIgnoreCase));

                    if (buds != null && !string.IsNullOrEmpty(buds.Address))
                    {
                        NSUserDefaults.StandardUserDefaults.SetString(buds.Address, MacAddressDefaultsKey);
                        NSUserDefaults.StandardUserDefaults.Synchronize();
                        Log($"Auto-saved Buds MAC from DB: {buds.Address}");
                    }

                    return devices.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log($"ReadBluetoothPairingDatabase error at {path}: {ex.Message}");
            }
        }

        return Array.Empty<BluetoothDevice>();
    }

    private void TriggerScan()
    {
        if (_btManager == nint.Zero) return;
        try
        {
            MsgSendVoidP(_btManager, Selector.GetHandle("setDeviceScanningEnabled:"), (nint)1);
            Log("TriggerScan: setDeviceScanningEnabled:YES");
        }
        catch (Exception ex) { Log($"TriggerScan error: {ex.Message}"); }
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
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
                    "BluetoothManager 未初始化");

            nint targetDevice = nint.Zero;

            // Try deviceFromAddressString: (confirmed correct method name from dump)
            try
            {
                using var nsAddr = new NSString(macAddress);
                targetDevice = MsgSendP(_btManager, Selector.GetHandle("deviceFromAddressString:"), nsAddr.Handle);
                Log($"deviceFromAddressString: → 0x{targetDevice:X}");

                if (targetDevice == nint.Zero)
                {
                    targetDevice = MsgSendP(_btManager, Selector.GetHandle("deviceFromIdentifier:"), nsAddr.Handle);
                    Log($"deviceFromIdentifier: → 0x{targetDevice:X}");
                }
            }
            catch (Exception ex) { Log($"deviceFromAddressString: error: {ex.Message}"); }

            if (targetDevice == nint.Zero)
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
                    $"无法获取设备对象（deviceFromAddressString: 返回 nil）。" +
                    $"BluetoothManager powered={MsgSendBool(_btManager, Selector.GetHandle("powered"))}");

            _connectedDevice = targetDevice;
            Log("Calling connectDevice:");
            MsgSendVoidP(_btManager, Selector.GetHandle("connectDevice:"), targetDevice);

            await Task.Delay(3000, cancelToken);

            bool isConnected = false;
            try { isConnected = MsgSendBool(targetDevice, Selector.GetHandle("isConnected")); }
            catch { }
            Log($"isConnected after connectDevice: = {isConnected}");

            if (!isConnected)
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
                    "connectDevice: 后 isConnected 仍为 false。XPC 连接可能被 bluetoothd 拒绝。");

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
