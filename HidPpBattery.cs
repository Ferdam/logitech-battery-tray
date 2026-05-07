using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace G703BatteryMonitor;

/// <summary>
/// Communicates with a Logitech G703 (or any HID++ 2.0 wireless mouse)
/// via hidapi to read battery percentage.
///
/// Protocol overview:
///   1. Open the HID device (Lightspeed/Unifying receiver or direct BT)
///   2. Send a HID++ 2.0 "GetFeatureIndex" request for feature 0x1000
///      (Battery Unified Level Status) or 0x1001 (Battery Voltage)
///   3. Using the returned feature index, call GetBatteryLevel / GetBatteryCapability
///   4. Parse the percentage from the response
///
/// Packet layout (20-byte long report, report ID 0x11):
///   [0]  Report ID  = 0x11
///   [1]  DeviceIdx  = 0xFF (short) or device index
///   [2]  FeatureIdx = obtained from root feature query
///   [3]  FunctionId | SoftwareId  (upper nibble = fn, lower = SW tag 0x0)
///   [4..19] Parameters / Response data
/// </summary>
public class HidPpBattery : IDisposable
{
    // ── hidapi P/Invoke ────────────────────────────────────────────────────────
    // hidapi.dll must be alongside the exe (copy from LGSTrayBattery release or
    // build from https://github.com/libusb/hidapi )
    private const string HidApiDll = "hidapi.dll";

    [DllImport(HidApiDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int hid_init();

    [DllImport(HidApiDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int hid_exit();

    [DllImport(HidApiDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr hid_open(ushort vendorId, ushort productId, IntPtr serialNumber);

    [DllImport(HidApiDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr hid_enumerate(ushort vendorId, ushort productId);

    [DllImport(HidApiDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void hid_free_enumeration(IntPtr devs);

    [DllImport(HidApiDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void hid_close(IntPtr device);

    [DllImport(HidApiDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int hid_write(IntPtr device, byte[] data, UIntPtr length);

    [DllImport(HidApiDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int hid_read_timeout(IntPtr device, byte[] data, UIntPtr length, int milliseconds);

    [DllImport(HidApiDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int hid_set_nonblocking(IntPtr device, int nonblock);

    [StructLayout(LayoutKind.Sequential)]
    private struct HidDeviceInfo
    {
        public IntPtr path;
        public ushort vendor_id;
        public ushort product_id;
        public IntPtr serial_number;
        public ushort release_number;
        public IntPtr manufacturer_string;
        public IntPtr product_string;
        public ushort usage_page;
        public ushort usage;
        public int interface_number;
        public IntPtr next;
    }

    [DllImport(HidApiDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr hid_open_path(IntPtr path);

    // ── Logitech USB IDs ───────────────────────────────────────────────────────
    // Vendor: Logitech
    private const ushort LogitechVid = 0x046D;

    // Known Lightspeed / Unifying receiver PIDs used with G703 Hero
    // 0xC539 = Lightspeed receiver (most common for G703)
    // 0xC52B = Unifying receiver
    // 0xC547 = Bolt receiver
    private static readonly ushort[] ReceiverPids = { 0xC539, 0xC52B, 0xC547 };

    // Usage page 0xFF00 = Logitech vendor-specific (the one we need for HID++)
    private const ushort HidppUsagePage = 0xFF00;

    // ── HID++ 2.0 constants ────────────────────────────────────────────────────
    private const byte ReportIdShort = 0x10; // 7-byte
    private const byte ReportIdLong  = 0x11; // 20-byte  ← we use this exclusively
    private const byte DeviceIndex   = 0xFF; // 0xFF = receiver itself for discovery;
                                             // paired device is usually 0x01 for Lightspeed
    private const byte SoftwareId   = 0x0B; // arbitrary SW tag (non-zero to avoid collision)

    // Root feature (always at index 0) — used to look up other feature indices
    private const ushort FeatureIdRoot              = 0x0000;
    // Battery features — we probe in priority order
    private const ushort FeatureIdBatteryStatus     = 0x1000; // percentage (older)
    private const ushort FeatureIdBatteryVoltage    = 0x1001; // voltage (G703 Hero uses this)
    private const ushort FeatureIdUnifiedBattery    = 0x1004; // unified (newest)

    // Function codes within each feature (upper nibble of byte[3])
    private const byte FnGetFeatureIndex  = 0x00; // Root: GetFeature(featureId) → featureIndex
    private const byte FnGetBatteryStatus = 0x00; // 0x1000: GetBatteryLevelStatus → level, fullLevel, status
    private const byte FnGetBatteryVoltage= 0x00; // 0x1001: GetBatteryLevelStatus → voltage, status
    private const byte FnGetCapabilities  = 0x00; // 0x1004: GetCapabilities
    private const byte FnGetStatus        = 0x10; // 0x1004: GetStatus (fn=1, upper nibble)

    private IntPtr _device = IntPtr.Zero;
    private bool _disposed;

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to open the first compatible Logitech receiver/device found.
    /// Returns true if a device was opened successfully.
    /// </summary>
    public bool TryOpen()
    {
        hid_init();

        foreach (var pid in ReceiverPids)
        {
            IntPtr devEnum = hid_enumerate(LogitechVid, pid);
            IntPtr cur = devEnum;

            while (cur != IntPtr.Zero)
            {
                var info = Marshal.PtrToStructure<HidDeviceInfo>(cur);

                // We want usage page FF00 (vendor HID++) — that's the control interface
                if (info.usage_page == HidppUsagePage)
                {
                    IntPtr handle = hid_open_path(info.path);
                    if (handle != IntPtr.Zero)
                    {
                        hid_free_enumeration(devEnum);
                        _device = handle;
                        hid_set_nonblocking(_device, 0); // blocking mode
                        return true;
                    }
                }

                cur = info.next;
            }

            hid_free_enumeration(devEnum);
        }

        return false;
    }

    /// <summary>
    /// Reads the battery percentage from the mouse.
    /// Returns -1 if the device is unreachable (e.g., mouse is off / asleep).
    /// </summary>
    public int ReadBatteryPercent()
    {
        if (_device == IntPtr.Zero) return -1;

        // Try features in priority order: 0x1004 > 0x1001 > 0x1000
        // The G703 Hero typically exposes 0x1001 (voltage-based)
        // but we probe all three for robustness.

        byte idx;

        // ── Try 0x1004 (Unified Battery) ──────────────────────────────────────
        idx = GetFeatureIndex(FeatureIdUnifiedBattery);
        if (idx > 0)
        {
            int pct = QueryUnifiedBattery(idx);
            if (pct >= 0) return pct;
        }

        // ── Try 0x1001 (Battery Voltage) ──────────────────────────────────────
        idx = GetFeatureIndex(FeatureIdBatteryVoltage);
        if (idx > 0)
        {
            int pct = QueryBatteryVoltage(idx);
            if (pct >= 0) return pct;
        }

        // ── Try 0x1000 (Battery Status) ───────────────────────────────────────
        idx = GetFeatureIndex(FeatureIdBatteryStatus);
        if (idx > 0)
        {
            int pct = QueryBatteryStatus(idx);
            if (pct >= 0) return pct;
        }

        return -1;
    }

    // ── HID++ helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a Root feature (0x0000) GetFeature request and returns the
    /// feature index for the requested feature ID, or 0 if not supported.
    /// </summary>
    private byte GetFeatureIndex(ushort featureId)
    {
        // Try device index 0x01 first (paired Lightspeed device), then 0xFF (receiver)
        foreach (byte devIdx in new byte[] { 0x01, 0xFF })
        {
            byte[] req = BuildLongRequest(devIdx, featureIndex: 0x00,
                functionId: FnGetFeatureIndex,
                param0: (byte)(featureId >> 8),
                param1: (byte)(featureId & 0xFF));

            byte[]? resp = SendAndReceive(req);
            // Skip HID++ 1.0 errors (resp[2]==0x8F) and HID++ 2.0 errors (resp[2]==0xFF).
            // A valid response carries the feature index in byte[4].
            if (resp != null && resp[2] != 0x8F && resp[2] != 0xFF && resp[4] != 0)
                return resp[4];
        }
        return 0;
    }

    // Feature 0x1000 — returns percentage directly
    private int QueryBatteryStatus(byte featureIdx)
    {
        byte[] req = BuildLongRequest(0x01, featureIdx, FnGetBatteryStatus, 0, 0);
        byte[]? resp = SendAndReceive(req);
        if (resp == null) return -1;
        // resp[4] = battery level (0-100)
        // resp[5] = next reported level
        // resp[6] = battery status flags
        return resp[4];
    }

    // Feature 0x1001 — voltage in mV, convert to % via standard LiPo curve
    private int QueryBatteryVoltage(byte featureIdx)
    {
        byte[] req = BuildLongRequest(0x01, featureIdx, FnGetBatteryVoltage, 0, 0);
        byte[]? resp = SendAndReceive(req);
        if (resp == null) return -1;
        int voltageMillivolts = (resp[4] << 8) | resp[5];
        if (voltageMillivolts <= 0) return -1;
        return VoltageToBatteryPercent(voltageMillivolts);
    }

    // Feature 0x1004 — Unified Battery: GetStatus (fn=1) returns level enum + percentage
    private int QueryUnifiedBattery(byte featureIdx)
    {
        // GetStatus = function 1 → upper nibble of byte[3] = 0x10
        byte[] req = BuildLongRequest(0x01, featureIdx, functionId: 0x10, 0, 0);
        byte[]? resp = SendAndReceive(req);
        if (resp == null) return -1;
        // resp[4] = state of charge (0-100) for devices that report it
        // resp[5] = level enum (full/good/low/critical)
        // resp[6] = flags (charging etc.)
        return resp[4]; // direct percentage
    }

    /// <summary>
    /// Builds a 20-byte long HID++ 2.0 request packet.
    /// Byte layout:
    ///   [0]  0x11 (long report ID)
    ///   [1]  device index
    ///   [2]  feature index
    ///   [3]  (functionId << 4) | softwareId
    ///   [4]  param0
    ///   [5]  param1
    ///   [6..19] zeros
    /// </summary>
    private static byte[] BuildLongRequest(byte deviceIndex, byte featureIndex,
        byte functionId, byte param0, byte param1)
    {
        var pkt = new byte[20];
        pkt[0] = ReportIdLong;
        pkt[1] = deviceIndex;
        pkt[2] = featureIndex;
        pkt[3] = (byte)((functionId & 0xF0) | (SoftwareId & 0x0F));
        pkt[4] = param0;
        pkt[5] = param1;
        return pkt;
    }

    /// <summary>
    /// Sends a HID++ request over the interrupt OUT pipe and reads back interrupt IN
    /// reports until one matches our request (or the budget expires).
    ///
    /// The receiver multiplexes button/wheel notifications onto the same pipe, so we
    /// can't just take the first packet — we have to filter by report ID, device
    /// index, feature index and software-id tag.
    /// </summary>
    private byte[]? SendAndReceive(byte[] request)
    {
        int written = hid_write(_device, request, (UIntPtr)request.Length);
        if (written < 0) return null;

        var response = new byte[20];
        int deadline = Environment.TickCount + 500; // 500ms total budget per request

        while (Environment.TickCount < deadline)
        {
            Array.Clear(response, 0, response.Length);
            int read = hid_read_timeout(_device, response, (UIntPtr)response.Length, 200);
            if (read <= 0) continue;

            // Must be a long HID++ report from the same device index we addressed.
            if (response[0] != request[0]) continue;
            if (response[1] != request[1]) continue;

            // HID++ 2.0 error: byte[2]=0xFF, then [3]=origFeatureIdx, [4]=orig fn|swId
            if (response[2] == 0xFF && response[3] == request[2] && response[4] == request[3])
                return response;

            // HID++ 1.0 error: byte[2]=0x8F, then [3]=subId, [4]=address
            if (response[2] == 0x8F && response[3] == request[2] && response[4] == request[3])
                return response;

            // Normal response: feature index and (function|swId) tag must echo the request.
            if (response[2] == request[2] && response[3] == request[3])
                return response;

            // Otherwise it's an unrelated notification — keep reading.
        }
        return null;
    }

    /// <summary>
    /// Converts a LiPo voltage (mV) to an approximate battery percentage using
    /// a standard 3.7V cell discharge curve — same approach as LGSTrayBattery's
    /// native mode (vs. the device-specific lookup table G HUB uses).
    ///
    /// Clamps to [0, 100].
    /// </summary>
    private static int VoltageToBatteryPercent(int mv)
    {
        // Discharge curve reference points for a 3.7V nominal LiPo:
        // 4200mV = 100%, 3700mV ≈ 50%, 3400mV ≈ 10%, 3200mV = 0%
        // Linear interpolation between segments.
        if (mv >= 4200) return 100;
        if (mv >= 4000) return 80 + (mv - 4000) * 20 / 200;
        if (mv >= 3800) return 60 + (mv - 3800) * 20 / 200;
        if (mv >= 3600) return 30 + (mv - 3600) * 30 / 200;
        if (mv >= 3400) return 10 + (mv - 3400) * 20 / 200;
        if (mv >= 3200) return     (mv - 3200) * 10 / 200;
        return 0;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_device != IntPtr.Zero)
            {
                hid_close(_device);
                _device = IntPtr.Zero;
            }
            hid_exit();
            _disposed = true;
        }
    }
}
