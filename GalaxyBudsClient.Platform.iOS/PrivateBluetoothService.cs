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
            Log("[BT-Fix] GetDevicesAsync invoked (v2) with connected selector fix");
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
    /// Reads the iOS Bluetooth pairing database to extract device MAC addresses.
    /// First enumerates known directories to find the actual file path.
    /// </summary>
    private BluetoothDevice[] ReadBluetoothPairingDatabase()
    {
        // Enumerate Bluetooth directories to find what files actually exist
        var btDirs = new[]
        {
            "/private/var/mobile/Library/Bluetooth",
            "/var/mobile/Library/Bluetooth",
            "/private/var/preferences",
            "/var/preferences",
        };

        foreach (var dir in btDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    var files = Directory.GetFiles(dir);
                    Log($"Dir {dir}: {files.Length} files");
                    foreach (var f in files)
                        Log($"  file: {Path.GetFileName(f)}");
                }
                else
                {
                    Log($"Dir not accessible: {dir}");
                }
            }
            catch (Exception ex) { Log($"Dir enum error {dir}: {ex.Message}"); }
        }

        // Try known plist paths (expanded list)
        var candidatePaths = new[]
        {
            "/private/var/mobile/Library/Bluetooth/com.apple.MobileBluetooth.devices.plist",
            "/private/var/mobile/Library/Bluetooth/devices.plist",
            "/private/var/mobile/Library/Bluetooth/BTServer.plist",
            "/var/mobile/Library/Bluetooth/com.apple.MobileBluetooth.devices.plist",
            "/var/mobile/Library/Bluetooth/devices.plist",
            "/private/var/preferences/com.apple.MobileBluetooth.devices.plist",
            "/var/preferences/com.apple.MobileBluetooth.devices.plist",
            "/private/var/mobile/Library/Preferences/com.apple.MobileBluetooth.plist",
        };

        foreach (var path in candidatePaths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                Log($"Found BT plist: {path}");

                var plistData = NSData.FromFile(path);
                if (plistData == null) continue;

                NSError? err;
                NSPropertyListFormat fmt = NSPropertyListFormat.Binary;
                var obj = NSPropertyListSerialization.PropertyListWithData(
                    plistData, NSPropertyListReadOptions.Immutable, ref fmt, out err);

                if (obj == null) { Log($"Parse failed: {err?.LocalizedDescription}"); continue; }

                Log($"Parsed {path}: type={obj.GetType().Name}");
                var devices = ParseBluetoothPlist(obj);
                if (devices.Length > 0) return devices;
            }
            catch (Exception ex) { Log($"Error reading {path}: {ex.Message}"); }
        }

        return Array.Empty<BluetoothDevice>();
    }

    private BluetoothDevice[] ParseBluetoothPlist(NSObject plistRoot)
    {
        var result = new List<BluetoothDevice>();
        var dict = plistRoot as NSDictionary;
        if (dict == null) return result.ToArray();

        Log($"Plist top-level keys ({dict.Count}): {string.Join(", ", dict.Keys.Take(5).Select(k => k.ToString()))}");

        // Format 1: { "AA:BB:CC:DD:EE:FF" = { Name = "..."; ... } }
        foreach (var key in dict.Keys)
        {
            var keyStr = key.ToString() ?? "";
            if (keyStr.Contains(":") || keyStr.Contains("-"))
            {
                var sub = dict.ObjectForKey(key) as NSDictionary;
                if (sub == null) continue;
                var name = (sub["Name"] ?? sub["DefaultName"] ?? sub["ProductName"])?.ToString() ?? keyStr;
                Log($"  Found device: {name} @ {keyStr}");
                result.Add(new BluetoothDevice(name, keyStr, true, false, new BluetoothCoD(0), null));
            }
        }

        // Format 2: { "devices" = [ { Address = "...", Name = "..." }, ... ] }
        if (result.Count == 0)
        {
            var devicesArr = (dict["devices"] ?? dict["Devices"]) as NSArray;
            if (devicesArr != null)
            {
                for (nuint i = 0; i < devicesArr.Count; i++)
                {
                    var item = devicesArr.GetItem<NSDictionary>(i);
                    var addr = (item?["Address"] ?? item?["address"])?.ToString() ?? "";
                    var name = (item?["Name"] ?? item?["name"])?.ToString() ?? addr;
                    if (!string.IsNullOrEmpty(addr))
                    {
                        Log($"  Found device (arr): {name} @ {addr}");
                        result.Add(new BluetoothDevice(name, addr, true, false, new BluetoothCoD(0), null));
                    }
                }
            }
        }

        // Auto-save the first Galaxy Buds device found
        var buds = result.FirstOrDefault(d =>
            d.Name.Contains("Buds", StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains("Galaxy", StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains("Samsung", StringComparison.OrdinalIgnoreCase));

        if (buds != null && !string.IsNullOrEmpty(buds.Address))
        {
            NSUserDefaults.StandardUserDefaults.SetString(buds.Address, MacAddressDefaultsKey);
            NSUserDefaults.StandardUserDefaults.Synchronize();
            Log($"Auto-saved Buds MAC: {buds.Address}");
        }

        return result.ToArray();
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
                var connected = MsgSendBool(devPtr, Selector.GetHandle("connected"));
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
            Log($"[BT-Fix] ConnectAsync invoked (v2): MAC={macAddress}");
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
                // removed deviceFromIdentifier fallback due to crash
            }
            catch (Exception ex) { Log($"deviceFromAddressString: error: {ex.Message}"); }

            if (targetDevice == nint.Zero)
            {
                Log("deviceFromAddressString: returned nil. Manually searching pairedDevices list...");
                    var listPtr = MsgSend(_btManager, Selector.GetHandle("pairedDevices"));
                    if (listPtr != nint.Zero)
                    {
                        var count = MsgSendUint(listPtr, Selector.GetHandle("count"));
                        for (nuint i = 0; i < (nuint)count; i++)
                        {
                            var devPtr = MsgSendP(listPtr, Selector.GetHandle("objectAtIndex:"), (nint)i);
                            var addr = GetNSString(devPtr, "address");
                            if (string.Equals(addr, macAddress, StringComparison.OrdinalIgnoreCase))
                            {
                                targetDevice = devPtr;
                                Log($"Found manual match for {macAddress} at 0x{targetDevice:X}");
                                break;
                            }
                        }
                    }
                }
            if (targetDevice == nint.Zero)
                throw new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed,
                    $"无法获取设备对象（deviceFromAddressString: 返回 nil）。" +
                    $"BluetoothManager powered={MsgSendBool(_btManager, Selector.GetHandle("powered"))}");

            _connectedDevice = targetDevice;
            Log("Calling connectDevice:");
            MsgSendVoidP(_btManager, Selector.GetHandle("connectDevice:"), targetDevice);

            await Task.Delay(3000, cancelToken);

            bool isConnected = false;
            try { isConnected = MsgSendBool(targetDevice, Selector.GetHandle("connected")); }
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
