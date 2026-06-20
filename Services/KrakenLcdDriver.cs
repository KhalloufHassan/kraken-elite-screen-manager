using System;
using System.Collections.Generic;
using System.Linq;
using HidSharp;
using LibUsbDotNet;
using LibUsbDotNet.Info;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace KrakenEliteScreenManager.Services;

/// <summary>
/// Native USB driver for the NZXT Kraken 2023 Elite LCD (1e71:300c), ported
/// from liquidctl's KrakenZ3 driver. Two channels (hidapi + pyusb split):
///   - HID control (HidSharp): 64-byte reports for brightness/orientation/buckets.
///   - bulk image (LibUsbDotNet): endpoint 0x02 for the GIF payload.
///
/// PushImage uploads a GIF into a bucket; the firmware then loops it natively.
/// ShowLiquid restores the built-in coolant-temperature screen.
/// </summary>
public sealed class KrakenLcdDriver : IDisposable
{
    public const int VendorId = 0x1E71;
    public const int ProductId = 0x300C;
    public const int Width = 640;
    public const int Height = 640;

    private const int ReportLength = 64;
    private const int LcdTotalMemory = 24320; // in 1024-byte units
    private const int BulkChunk = 1024 * 1024; // bulk write chunk size
    private const int BulkTimeoutMs = 5000;

    // Fixed magic prefix for every bulk transfer header (from liquidctl).
    private static readonly byte[] BulkMagic =
        { 0x12, 0xFA, 0x01, 0xE8, 0xAB, 0xCD, 0xEF, 0x98, 0x76, 0x54, 0x32, 0x10 };

    private readonly Action<string> _log;

    private HidStream? _hid;
    private int _hidOutOffset; // 1 if the HID report carries a leading report-id byte
    private int _hidInOffset;

    private UsbContext? _usb;
    private IUsbDevice? _bulkDevice;
    private UsbEndpointWriter? _bulkOut;
    private int _claimedInterface = -1;

    // Index of the bucket currently on screen; never overwritten while live.
    private int _activeBucket = -1;

    public KrakenLcdDriver(Action<string>? log = null) => _log = log ?? (_ => { });

    public void Open()
    {
        OpenHid();
        OpenBulk();
        _log("Kraken LCD driver opened.");
    }

    private void OpenHid()
    {
        var dev = DeviceList.Local.GetHidDevices(VendorId, ProductId).FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Kraken HID interface {VendorId:x4}:{ProductId:x4} not found. " +
                "Is the device connected and the udev rule installed?");

        if (!dev.TryOpen(out _hid))
            throw new InvalidOperationException(
                "Failed to open the Kraken HID interface (permission denied?). " +
                "Install the udev rule: sudo ./scripts/install-udev.sh, then replug.");

        // HidSharp report lengths include the report-id byte; NZXT uses report id 0.
        _hidOutOffset = dev.GetMaxOutputReportLength() > ReportLength ? 1 : 0;
        _hidInOffset = dev.GetMaxInputReportLength() > ReportLength ? 1 : 0;
        _hid!.ReadTimeout = 150;   // device replies in a few ms; keeps Command() snappy
        _hid!.WriteTimeout = 2000; // never hang forever on a stuck device — fail and let us recover
        _log($"HID open (out+{_hidOutOffset}, in+{_hidInOffset}).");
    }

    private void OpenBulk()
    {
        _usb = new UsbContext();
        var finder = new UsbDeviceFinder { Vid = VendorId, Pid = ProductId };
        _bulkDevice = _usb.Find(finder)
            ?? throw new InvalidOperationException(
                $"Kraken USB device {VendorId:x4}:{ProductId:x4} not found for bulk transfer.");

        _bulkDevice.Open();
        // The HID interface is owned by the kernel/hidraw; only detach for the
        // vendor-specific interface we claim. (No-op if not a UsbDevice.)
        if (_bulkDevice is UsbDevice ud)
            ud.SetAutoDetachKernelDriver(true);

        var iface = FindBulkInterface(_bulkDevice, out var epAddr)
            ?? throw new InvalidOperationException("Could not find the bulk OUT interface (EP 0x02).");

        _bulkDevice.ClaimInterface(iface.Number);
        _claimedInterface = iface.Number;
        _bulkOut = _bulkDevice.OpenEndpointWriter((WriteEndpointID)epAddr);
        _log($"Bulk open (interface {iface.Number}, EP 0x{epAddr:x2}).");
    }

    private static UsbInterfaceInfo? FindBulkInterface(IUsbDevice device, out byte epAddr)
    {
        epAddr = 0x02;
        foreach (var cfg in device.Configs)
        foreach (var iface in cfg.Interfaces)
        foreach (var ep in iface.Endpoints)
        {
            // OUT endpoint (top bit clear) with address 0x02.
            if ((ep.EndpointAddress & 0x80) == 0 && (ep.EndpointAddress & 0x0F) == 0x02)
            {
                epAddr = ep.EndpointAddress;
                return iface;
            }
        }
        return null;
    }

    // --- HID control channel -------------------------------------------------

    private void Write(params byte[] data)
    {
        if (_hid is null) throw new InvalidOperationException("HID not open.");
        var buf = new byte[ReportLength + _hidOutOffset];
        Array.Copy(data, 0, buf, _hidOutOffset, Math.Min(data.Length, ReportLength));
        _hid.Write(buf);
    }

    /// <summary>Read one report (aligned so index 0 is the first real byte), or null on timeout.</summary>
    private byte[]? ReadReport()
    {
        try
        {
            var raw = new byte[ReportLength + _hidInOffset];
            int read = _hid!.Read(raw);
            if (read <= 0) return null;
            var msg = new byte[ReportLength];
            Array.Copy(raw, _hidInOffset, msg, 0, Math.Min(ReportLength, Math.Max(0, read - _hidInOffset)));
            return msg;
        }
        catch (TimeoutException) { return null; }
    }

    /// <summary>
    /// Write a command, then read reports until one matches the expected reply
    /// prefix (reply = command[0]+1, command[1]), skipping stray/status reports.
    /// Returns the matching report, or null if none arrived. This is what keeps
    /// rapid streaming in sync — single reads drift under load.
    /// </summary>
    private byte[]? Command(byte b0, byte b1, params byte[] rest)
    {
        var data = new byte[2 + rest.Length];
        data[0] = b0; data[1] = b1;
        Array.Copy(rest, 0, data, 2, rest.Length);
        Write(data);

        for (int i = 0; i < 24; i++)
        {
            var msg = ReadReport();
            if (msg is null) return null;
            if (msg[0] == (byte)(b0 + 1) && msg[1] == b1) return msg;
        }
        return null;
    }

    /// <summary>Discard any pending/stale reports so the next Command() starts clean.</summary>
    private void Drain()
    {
        var saved = _hid!.ReadTimeout;
        _hid.ReadTimeout = 4;
        try { while (ReadReport() is not null) { } }
        finally { _hid.ReadTimeout = saved; }
    }

    private void BulkWrite(byte[] data) => BulkWrite(data, BulkChunk);

    private void BulkWrite(byte[] data, int chunk)
    {
        if (_bulkOut is null) throw new InvalidOperationException("Bulk endpoint not open.");
        for (int i = 0; i < data.Length; i += chunk)
        {
            int len = Math.Min(chunk, data.Length - i);
            var err = _bulkOut.Write(data, i, len, BulkTimeoutMs, out int sent);
            if (err != Error.Success || sent != len)
                throw new InvalidOperationException($"Bulk write failed: {err} ({sent}/{len} bytes).");
        }
    }

    // --- public control ------------------------------------------------------

    /// <summary>Write a command, then read one report back (aligned). Plain
    /// in-order single read, matching liquidctl.</summary>
    private byte[] WriteThenRead(params byte[] data)
    {
        Write(data);
        return ReadReport() ?? new byte[ReportLength];
    }

    public (int brightness, int orientation) ReadLcdInfo()
    {
        Drain();
        var msg = Command(0x30, 0x01); // prefix-matched: reliable even right after init
        return msg is null ? (50, 0) : (msg[0x18], msg[0x1A]);
    }

    public void SetBrightness(int percent, int orientation = 0)
    {
        percent = Math.Clamp(percent, 0, 100);
        Write(0x30, 0x02, 0x01, (byte)percent, 0x00, 0x00, 0x01, (byte)orientation);
    }

    /// <summary>Set the panel's saved orientation (0/90/180/270°). This is what the
    /// built-in liquid screen uses, so restoring it keeps the default display upright.</summary>
    public void SetOrientation(int degrees)
    {
        var (brightness, _) = ReadLcdInfo();
        int idx = ((degrees / 90) % 4 + 4) % 4;
        Write(0x30, 0x02, 0x01, (byte)Math.Clamp(brightness, 1, 100), 0x00, 0x00, 0x01, (byte)idx);
    }

    /// <summary>Restore the built-in liquid-temperature screen (un-blanks the LCD).</summary>
    public void ShowLiquid() => SwitchBucket(0, 0x02);

    /// <summary>
    /// USB-reset the device (re-enumerate) to recover from a wedged state without a
    /// reboot. Only works while the device is still on the bus.
    /// </summary>
    public static bool ResetDevice(Action<string>? log = null)
    {
        try
        {
            using var ctx = new UsbContext();
            var dev = ctx.Find(new UsbDeviceFinder { Vid = VendorId, Pid = ProductId });
            if (dev is null) { log?.Invoke("Reset: device not found (off the bus — power-cycle needed)."); return false; }
            dev.Open();
            dev.ResetDevice();
            dev.Dispose();
            log?.Invoke("USB reset issued.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Reset failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Upload a GIF (asset mode 0x01 — the format fw 2.x displays). Finds a free
    /// bucket, places it, transfers, switches. The firmware then loops the GIF.
    /// </summary>
    public void PushImage(byte[] data, byte assetMode = 0x01)
    {
        var lenLe = BitConverter.GetBytes((uint)data.Length); // 4 bytes LE
        var header = BulkMagic
            .Concat(new byte[] { assetMode, 0x00, 0x00, 0x00 })
            .Concat(lenLe)
            .ToArray();

        // Faithful liquidctl _send_data — the exact sequence that fully
        // displayed the rainbow. Find a free bucket, place it, transfer, switch.
        WriteThenRead(0x36, 0x03);
        var buckets = QueryBuckets();
        int bucketIndex = FindNextUnoccupiedBucket(buckets);
        bucketIndex = PrepareBucket(bucketIndex != -1 ? bucketIndex : 0, bucketIndex == -1);

        int dataUnits = (int)Math.Ceiling((header.Length + data.Length) / 1024.0);
        var sizeBytes = BitConverter.GetBytes((ushort)dataUnits);

        var memStart = GetBucketMemoryOffset(buckets, bucketIndex, dataUnits);
        if (memStart is null)
        {
            DeleteAllBuckets();
            bucketIndex = 0;
            memStart = new byte[] { 0x00, 0x00 };
        }

        SetupBucket(bucketIndex, bucketIndex + 1, memStart, sizeBytes);
        WriteThenRead(0x36, 0x01, (byte)bucketIndex);
        BulkWrite(header);
        BulkWrite(data);
        Write(0x36, 0x02);
        SwitchBucket(bucketIndex);
        _activeBucket = bucketIndex;
    }

    // --- raw BGR888 streaming (CAM-parity, asset mode 0x09) ------------------
    // Reverse-engineered from a USB capture of NZXT CAM driving the 300c: stream a
    // full 640x640 BGR888 frame to the bulk endpoint behind a 20-byte header, with
    // NO per-frame bucket setup/switch — that's how CAM stays smooth without wedging.

    private static readonly byte[] StreamLut1 =
        new byte[] { 0x72, 0x01, 0x01, 0x00 }.Concat(Enumerable.Repeat((byte)0x3f, 41)).ToArray();
    private static readonly byte[] StreamLut2 =
        new byte[] { 0x72, 0x02, 0x01, 0x01 }.Concat(Enumerable.Repeat((byte)0x1f, 41)).ToArray();

    /// <summary>
    /// Put the LCD into CAM's live-streaming mode via the one-time HID init handshake
    /// captured from NZXT CAM. Call once, then PushFrameRaw() repeatedly.
    /// </summary>
    public void EnterStreamingMode(int percent = 80)
    {
        Drain();
        WriteThenRead(0x10, 0x02);
        WriteThenRead(0x70, 0x02, 0x01, 0xb8, 0x0b);
        WriteThenRead(0x74, 0x01);
        WriteThenRead(0x36, 0x04);
        WriteThenRead(0x30, 0x01);
        WriteThenRead(0x36, 0x03);
        WriteThenRead(0x30, 0x02, 0x00, 0x00, 0x00, 0x00, 0x1e);
        WriteThenRead(0x38, 0x01, 0x02);                                 // switch to liquid (clears)
        for (byte i = 0; i < 16; i++) WriteThenRead(0x32, 0x02, i);      // delete all 16 buckets
        WriteThenRead(0x30, 0x02, 0x01, (byte)Math.Clamp(percent, 0, 100), 0x00, 0x00, 0x00, 0x1e);
        Drain();
    }

    /// <summary>
    /// Stream one full 640x640 RGB888 frame (1,228,800 bytes) — CAM asset mode 0x09.
    /// Per-frame: LUTs -> start -> bulk header+data -> end. No bucket setup/switch.
    /// </summary>
    private const int StreamUrb = 245760; // CAM sends frame data in 245,760-byte bulk transfers

    public void PushFrameRaw(byte[] rgb888)
    {
        const int expected = Width * Height * 3;
        if (rgb888.Length != expected)
            throw new ArgumentException($"expected {expected} RGB888 bytes, got {rgb888.Length}");

        // ACK each HID command (flow control) so the bulk data never races ahead of "start".
        Drain();
        WriteThenRead(StreamLut1);
        WriteThenRead(StreamLut2);
        WriteThenRead(0x36, 0x01, 0x00, 0x01, 0x09);                    // start, asset mode 0x09

        var lenLe = BitConverter.GetBytes((uint)rgb888.Length);
        var header = BulkMagic.Concat(new byte[] { 0x09, 0x00, 0x00, 0x00 }).Concat(lenLe).ToArray();
        BulkWrite(header);
        BulkWrite(rgb888, StreamUrb);                                   // match CAM's 245,760-byte transfers

        WriteThenRead(0x36, 0x02);                                      // end
    }

    // --- bucket helpers (ported from liquidctl) ------------------------------

    private Dictionary<int, byte[]> QueryBuckets()
    {
        var buckets = new Dictionary<int, byte[]>();
        for (int i = 0; i < 16; i++)
        {
            var m = Command(0x30, 0x04, (byte)i);
            if (m is not null) buckets[i] = m;
        }
        return buckets;
    }

    private static int FindNextUnoccupiedBucket(Dictionary<int, byte[]> buckets)
    {
        foreach (var (index, info) in buckets)
        {
            bool occupied = false;
            for (int b = 15; b < info.Length; b++)
                if (info[b] != 0) { occupied = true; break; }
            if (!occupied) return index;
        }
        return -1;
    }

    private int PrepareBucket(int bucketIndex, bool bucketFilled)
    {
        if (bucketIndex >= 16) throw new InvalidOperationException("Reached max bucket.");
        if (!DeleteBucket(bucketIndex)) return PrepareBucket(bucketIndex + 1, true);
        if (bucketFilled) return PrepareBucket(bucketIndex, false);
        return bucketIndex;
    }

    private void DeleteAllBuckets()
    {
        SwitchBucket(0, 0x02); // liquid mode
        for (int i = 0; i < 16; i++) DeleteBucket(i);
    }

    private static byte[]? GetBucketMemoryOffset(Dictionary<int, byte[]> buckets, int bucketIndex, int dataUnits)
    {
        if (!buckets.TryGetValue(bucketIndex, out var cur)) return new byte[] { 0x00, 0x00 };
        int curOffset = cur[17] | (cur[18] << 8);
        int curSize = cur[19] | (cur[20] << 8);

        if (dataUnits <= curSize) return new[] { cur[17], cur[18] };

        int minOccupied = curOffset, maxOccupied = 0;
        bool overlap = false;
        foreach (var (idx, b) in buckets)
        {
            int start = b[17] | (b[18] << 8);
            int end = start + (b[19] | (b[20] << 8));
            if (end > maxOccupied) maxOccupied = end;
            if (start < minOccupied) minOccupied = start;
            if ((start > curOffset && start < curOffset + dataUnits) ||
                (start < curOffset && end > start) ||
                (start == curOffset && idx != bucketIndex))
                overlap = true;
        }

        if (!overlap) return new[] { cur[17], cur[18] };
        if (maxOccupied + dataUnits < LcdTotalMemory) return BitConverter.GetBytes((ushort)maxOccupied);
        if (dataUnits < minOccupied) return new byte[] { 0x00, 0x00 };
        return null;
    }

    private bool SetupBucket(int startIndex, int endIndex, byte[] memStart, byte[] sizeBytes)
    {
        // b14 isn't a reliable success byte on fw 2.x (returns 0x05); a reply at
        // all means the command was accepted.
        var msg = Command(0x32, 0x01, (byte)startIndex, (byte)endIndex,
            memStart[0], memStart[1], sizeBytes[0], sizeBytes[1], 0x01);
        return msg is not null;
    }

    private bool SwitchBucket(int index, byte mode = 0x04)
    {
        var msg = Command(0x38, 0x01, mode, (byte)index);
        return msg is not null;
    }

    private bool DeleteBucket(int index)
    {
        var msg = Command(0x32, 0x02, (byte)index);
        return msg is not null && msg[14] == 0x01;
    }

    public void Dispose()
    {
        try { _hid?.Dispose(); } catch { /* ignore */ }
        try
        {
            if (_bulkDevice is not null && _claimedInterface >= 0)
                _bulkDevice.ReleaseInterface(_claimedInterface);
        }
        catch { /* ignore */ }
        try { _bulkDevice?.Dispose(); } catch { /* ignore */ }
        try { _usb?.Dispose(); } catch { /* ignore */ }
    }
}
