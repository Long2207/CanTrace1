// ============================================================================
// BlfWriter.cs
// Writes a Vector BLF (Binary Logging Format) file. CANoe / CANalyzer can
// open the output.
//
// BLF format is proprietary and undocumented; this implementation follows the
// reverse-engineered structure used by python-can's BLFWriter, which is in
// turn based on Tobias Lorenz's C++ library.
//
// File layout:
//   FILE_HEADER (144 bytes, fixed)
//   [ LogContainer 0 ][ LogContainer 1 ] ...
//
// LogContainer = OBJ_HEADER + container_header + zlib-compressed body.
// Body = concatenation of [OBJ_HEADER + object_data]* (each object 4-byte aligned).
// Container is finalized & written every time uncompressed body exceeds ~128 KiB.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using CanTracer.Models;

namespace CanTracer.Services;

public sealed class BlfWriter : IDisposable
{
    // BLF object type IDs (from binlog_objects.h).
    private const ushort OBJ_TYPE_LOG_CONTAINER = 10;
    private const ushort OBJ_TYPE_CAN_MESSAGE2  = 86;  // classic CAN frame (preferred)
    private const ushort OBJ_TYPE_CAN_FD_MSG_64 = 101; // CAN FD frame

    private const uint  SIG_LOGG = 0x47474F4Cu; // "LOGG"
    private const uint  SIG_LOBJ = 0x4A424F4Cu; // "LOBJ"

    private const int FILE_HEADER_SIZE  = 144;
    private const int OBJ_HEADER_SIZE   = 16;
    private const int FLUSH_THRESHOLD   = 128 * 1024; // flush container when body ≥ 128 KiB

    private readonly FileStream _fs;
    private readonly DateTime   _startUtc;
    private readonly MemoryStream _body = new();   // uncompressed body of current container
    private long _firstTimestampNs = -1;
    private long _lastTimestampNs;
    private long _objectCount;
    private bool _disposed;
    private readonly object _lock = new();

    public BlfWriter(string path)
    {
        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _startUtc = DateTime.UtcNow;
        // Reserve space for the header — we patch it in Dispose() with final stats.
        _fs.Write(new byte[FILE_HEADER_SIZE], 0, FILE_HEADER_SIZE);
    }

    // -------------------------------------------------------------------------
    // Append one frame. Thread-safe; can be called from the RX thread.
    // -------------------------------------------------------------------------
    public void Write(CanMessage f, int channel)
    {
        if (_disposed) return;

        // Convert absolute frame time (seconds) into BLF timestamp (10 ns units).
        long ts100ns = (long)(f.Time * 10_000_000.0);

        lock (_lock)
        {
            if (_firstTimestampNs < 0) _firstTimestampNs = ts100ns;
            _lastTimestampNs = ts100ns;

            if (f.IsFd) WriteCanFd(f, channel, ts100ns);
            else        WriteCanClassic(f, channel, ts100ns);
            _objectCount++;

            if (_body.Length >= FLUSH_THRESHOLD) FlushContainer();
        }
    }

    // -------------------------------------------------------------------------
    // OBJ_HEADER (16 bytes) — common prefix for every object in a container.
    // -------------------------------------------------------------------------
    private static void WriteObjHeader(BinaryWriter w, ushort objType, uint totalLen, long ts100ns)
    {
        w.Write(SIG_LOBJ);                  // 4 — signature "LOBJ"
        w.Write((ushort)OBJ_HEADER_SIZE);   // 2 — header size
        w.Write((ushort)1);                 // 2 — header version
        w.Write(totalLen);                  // 4 — object size (header + body)
        w.Write(objType);                   // 2 — object type
        w.Write((ushort)0);                 // 2 — flags (0 = timestamp in 10 ns units, absolute)
        // Continued in caller: reserved + timestamp.
        // NOTE: python-can puts client index (2) + version (2) + timestamp (8) right after objType.
        // We follow that layout below.
        w.Write((ushort)0);                 // 2 — client index
        w.Write((ushort)0);                 // 2 — object version
        w.Write(ts100ns);                   // 8 — timestamp (signed, 10 ns)
    }

    // Wait — re-do: WriteObjHeader above writes 24 bytes. Let me fix that by
    // splitting into "base header (16)" + "extended fields (8)". The total
    // BaseObjectHeader (V1) is actually 16 bytes; the timestamp lives in the
    // ObjectHeader (32 bytes total). To keep it simple we always use V1 with
    // timestamp embedded — so headerSize should be 32 and we account for that.
    //
    // The cleanest fix is to use ONE helper that writes the 32-byte object
    // header (V1 with timestamp), then the type-specific body follows.
    // ------------------------------------------------------------------------

    private static void WriteHeader32(BinaryWriter w, ushort objType, uint totalLen, long ts100ns)
    {
        w.Write(SIG_LOBJ);                // 4
        w.Write((ushort)32);              // 2 — header size
        w.Write((ushort)1);               // 2 — header version (1 = ObjectHeader with timestamp)
        w.Write(totalLen);                // 4 — total object size (32 + body)
        w.Write(objType);                 // 2
        w.Write((ushort)0);               // 2 — flags
        w.Write((ushort)0);               // 2 — client index
        w.Write((ushort)0);               // 2 — object version
        w.Write(ts100ns);                 // 8 — timestamp (10 ns)
        // 4 bytes accounted by header, 4 for objSize, 4 type+flags, 4 client+ver, 8 ts → 28? Let's recount:
        // sig(4) + hsize(2) + hver(2) + osize(4) + otype(2) + flags(2) + cidx(2) + over(2) + ts(8) = 28.
        // Need 4 more bytes of padding to reach the documented 32-byte ObjectHeader.
        w.Write((uint)0);                 // 4 — reserved (pad to 32)
    }

    // -------------------------------------------------------------------------
    // CAN_MESSAGE2 body (24 bytes, after the 32-byte header → total 56 bytes).
    //   channel (uint16), flags (uint8), dlc (uint8), id (uint32),
    //   data[8], frameLength_us (uint32), bitCount (uint8), reserved[5]
    // -------------------------------------------------------------------------
    private void WriteCanClassic(CanMessage f, int channel, long ts100ns)
    {
        const uint TOTAL_LEN = 32 + 24;
        using var bw = new BinaryWriter(_body, System.Text.Encoding.UTF8, leaveOpen: true);
        WriteHeader32(bw, OBJ_TYPE_CAN_MESSAGE2, TOTAL_LEN, ts100ns);

        bw.Write((ushort)channel);                     // 1-based channel
        byte flags = 0;
        if (f.Direction == "Tx") flags |= 0x01;        // bit 0 = Tx
        if (f.IsExtended)        flags |= 0x80;        // bit 7 = extended ID indicator (per python-can)
        bw.Write(flags);                               // flags
        bw.Write((byte)Math.Min(f.Dlc, 15));           // dlc

        var arbId = f.IsExtended ? (f.Id | 0x80000000) : f.Id;
        bw.Write(arbId);                               // CAN ID (with ext bit)

        // data[8] — pad shorter payloads with zeroes
        var data = new byte[8];
        Array.Copy(f.Data, 0, data, 0, Math.Min(f.Data.Length, 8));
        bw.Write(data);

        bw.Write((uint)0);                             // frameLength (unknown — leave 0)
        bw.Write((byte)0);                             // bitCount (unknown)
        bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0); // reserved[3]

        AlignTo4(bw);
    }

    // -------------------------------------------------------------------------
    // CAN_FD_MESSAGE_64 body — at minimum 60 bytes + payload, payload padded
    // to multiple of 4 bytes. Layout per python-can BLFWriter.
    // -------------------------------------------------------------------------
    private void WriteCanFd(CanMessage f, int channel, long ts100ns)
    {
        // Payload padded to 4-byte boundary.
        int payloadLen = ((f.DataLength + 3) / 4) * 4;
        if (payloadLen < 4) payloadLen = 4;
        uint totalLen = (uint)(32 + 60 + payloadLen);

        using var bw = new BinaryWriter(_body, System.Text.Encoding.UTF8, leaveOpen: true);
        WriteHeader32(bw, OBJ_TYPE_CAN_FD_MSG_64, totalLen, ts100ns);

        bw.Write((byte)channel);                       // channel (uint8 here, not 16)
        bw.Write((byte)Math.Min(f.Dlc, 15));           // dlc
        bw.Write((byte)0);                             // valid data bytes — set below
        bw.Write((byte)0);                             // tx count
        bw.Write(f.IsExtended ? (f.Id | 0x80000000) : f.Id);   // ID
        bw.Write((uint)0);                             // frameLength

        uint flags = 0x1000;                           // EDL (extended data length) — FD frame
        if (f.IsBrs)                 flags |= 0x2000;  // BRS
        if (f.Direction == "Tx")     flags |= 0x0001;
        bw.Write(flags);                               // flagsExt (uint32)

        bw.Write((uint)0);                             // btrCfgArb
        bw.Write((uint)0);                             // btrCfgData
        bw.Write((uint)0);                             // timeOffsetBrsNs
        bw.Write((uint)0);                             // timeOffsetCrcDelNs
        bw.Write((ushort)0);                           // bitCount
        bw.Write((byte)0);                             // dir
        bw.Write((byte)f.DataLength);                  // extDataOffset
        bw.Write((uint)0);                             // crc
        bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0); // reserved (12 bytes)
        bw.Write((uint)0); bw.Write((uint)0);                    // (8 more bytes to reach 60)

        // Payload (padded to 4 bytes)
        var payload = new byte[payloadLen];
        Array.Copy(f.Data, 0, payload, 0, Math.Min(f.Data.Length, payloadLen));
        bw.Write(payload);
    }

    private static void AlignTo4(BinaryWriter bw)
    {
        long pos = bw.BaseStream.Position;
        int pad = (int)((4 - (pos % 4)) % 4);
        for (int i = 0; i < pad; i++) bw.Write((byte)0);
    }

    // -------------------------------------------------------------------------
    // Compress current _body and write it out as one LogContainer.
    // -------------------------------------------------------------------------
    private void FlushContainer()
    {
        if (_body.Length == 0) return;

        var uncompressed = _body.ToArray();
        _body.SetLength(0);

        // zlib compress
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var zs = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                zs.Write(uncompressed, 0, uncompressed.Length);
            compressed = ms.ToArray();
        }

        // Container header: 32-byte object header (type=LOG_CONTAINER) + 16-byte container header.
        //   container header: compression(uint16=2 for zlib), reserved(uint16),
        //                     uncompressed size (uint32), reserved (uint32 × 2)
        uint totalLen = (uint)(32 + 16 + compressed.Length);

        using var bw = new BinaryWriter(_fs, System.Text.Encoding.UTF8, leaveOpen: true);
        WriteHeader32(bw, OBJ_TYPE_LOG_CONTAINER, totalLen, 0);

        bw.Write((ushort)2);                   // compressionMethod: 2 = zlib deflate
        bw.Write((ushort)0);                   // reserved
        bw.Write((uint)uncompressed.Length);   // uncompressedFileSize
        bw.Write((uint)0); bw.Write((uint)0);  // reserved (8)

        bw.Write(compressed);

        // 4-byte align between containers
        long pos = _fs.Position;
        int pad = (int)((4 - (pos % 4)) % 4);
        for (int i = 0; i < pad; i++) _fs.WriteByte(0);
    }

    // -------------------------------------------------------------------------
    // Finalize: flush remaining body, then patch the file header.
    // -------------------------------------------------------------------------
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            FlushContainer();
            _fs.Flush();

            var stopUtc = DateTime.UtcNow;
            long uncompressedSize = 0; // optional; readers don't usually require this

            _fs.Seek(0, SeekOrigin.Begin);
            using var bw = new BinaryWriter(_fs, System.Text.Encoding.UTF8, leaveOpen: true);

            bw.Write(SIG_LOGG);                         // 4 — file signature
            bw.Write((uint)FILE_HEADER_SIZE);           // 4 — header size
            bw.Write((byte)1); bw.Write((byte)0);
            bw.Write((byte)0); bw.Write((byte)0);       // app id + 3 ver bytes
            bw.Write((byte)2); bw.Write((byte)0);
            bw.Write((byte)0); bw.Write((byte)0);       // binlog version
            bw.Write((ulong)_fs.Length);                // file size
            bw.Write((ulong)uncompressedSize);          // uncompressed body size (informational)
            bw.Write((uint)_objectCount);               // object count
            bw.Write((uint)_objectCount);               // count of objects read (= total)

            WriteSystemTime(bw, _startUtc);             // 16 — measurement start
            WriteSystemTime(bw, stopUtc);               // 16 — measurement stop
            // remaining bytes of the 144-byte header are zero-pad (already written at ctor).

            _fs.Flush();
            _fs.Dispose();
        }
        _body.Dispose();
    }

    // Windows SYSTEMTIME structure (16 bytes).
    private static void WriteSystemTime(BinaryWriter bw, DateTime utc)
    {
        bw.Write((ushort)utc.Year);
        bw.Write((ushort)utc.Month);
        bw.Write((ushort)utc.DayOfWeek);
        bw.Write((ushort)utc.Day);
        bw.Write((ushort)utc.Hour);
        bw.Write((ushort)utc.Minute);
        bw.Write((ushort)utc.Second);
        bw.Write((ushort)utc.Millisecond);
    }
}

// ----------------------------------------------------------------------------
// Recorder facade — holds the active BlfWriter and a bus→channel mapping.
// One BLF file aggregates all buses; each bus is mapped to a sequential
// channel index (1, 2, 3, ...).
// ----------------------------------------------------------------------------
public sealed class BlfRecorder : IDisposable
{
    private BlfWriter? _writer;
    private readonly Dictionary<string, int> _busToChannel = new();
    private int _frameCount;
    private string _path = "";

    public bool IsRecording => _writer != null;
    public int  FrameCount  => _frameCount;
    public string Path      => _path;

    public void Start(string path)
    {
        Stop();
        _writer = new BlfWriter(path);
        _busToChannel.Clear();
        _frameCount = 0;
        _path = path;
    }

    public void Stop()
    {
        var w = Interlocked.Exchange(ref _writer, null);
        w?.Dispose();
    }

    public void Write(CanMessage f)
    {
        var w = _writer;
        if (w is null) return;

        if (!_busToChannel.TryGetValue(f.BusName, out var channel))
        {
            channel = _busToChannel.Count + 1;
            _busToChannel[f.BusName] = channel;
        }
        w.Write(f, channel);
        Interlocked.Increment(ref _frameCount);
    }

    public void Dispose() => Stop();
}

public static class LogFolderManager
{
    /// <summary>Absolute path to the root "Logs" folder next to the running exe.</summary>
    public static string RootFolder
    {
        get
        {
            var exeDir = AppContext.BaseDirectory;
            var path = Path.Combine(exeDir, "Logs");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>Create a new timestamped subfolder under Logs/ and return its full path.</summary>
    public static string CreateNewSession()
    {
        var folder = Path.Combine(RootFolder, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Open the Logs/ root folder in Windows Explorer.</summary>
    public static void OpenRoot()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = RootFolder,
            UseShellExecute = true,
            Verb = "open"
        });
    }
}
