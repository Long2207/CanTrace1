// ============================================================================
// PcanService.cs (v6)
// Now implements ICanService. Same logic as before; signatures unified with
// VectorService so BusManager can talk to either via the interface.
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CanTracer.Models;
using TPCANBitrateFD = System.String;
using TPCANHandle = System.UInt16;
using TPCANTimestampFD = System.UInt64;

namespace CanTracer.Services;

public class PcanService : ICanService
{
    private ushort _channel = PcanChannels.PCAN_NONEBUS;
    private bool _isFd;
    private CancellationTokenSource? _cts;
    private Task? _rxTask;
    private readonly Stopwatch _sw = new();

    public bool IsConnected { get; private set; }

    public event Action<CanMessage>? FrameReceived;
    public event Action<string>?     ErrorOccurred;

    // -------------------------------------------------------------------------
    // ICanService surface
    // -------------------------------------------------------------------------

    public bool Connect(string channelId, string baud)
    {
        if (!TryResolveChannel(channelId, out var ch)) return false;
        return ConnectClassic(ch, ParseBaud(baud));
    }

    public bool ConnectFd(string channelId, string nominalKbit, string dataKbit)
    {
        if (!TryResolveChannel(channelId, out var ch)) return false;
        // Build a PEAK FD bitrate string from kbit values.
        // Standard preset for 80 MHz CAN clock, ISO mode.
        var fdString = BuildFdBitrate(nominalKbit, dataKbit);
        return ConnectFdRaw(ch, fdString);
    }

    private bool ConnectClassic(ushort channel, TPCANBaudrate baud)
    {
        Disconnect();
        var st = PCANBasic.Initialize(channel, baud);
        if (st != TPCANStatus.PCAN_ERROR_OK)
        {
            ErrorOccurred?.Invoke("PEAK Initialize: " + PCANBasic.ErrorText(st));
            return false;
        }
        _channel = channel; _isFd = false; IsConnected = true;
        return true;
    }

    private bool ConnectFdRaw(ushort channel, string fdBitrate)
    {
        Disconnect();
        var st = PCANBasic.InitializeFD(channel, fdBitrate);
        if (st != TPCANStatus.PCAN_ERROR_OK)
        {
            ErrorOccurred?.Invoke("PEAK InitializeFD: " + PCANBasic.ErrorText(st));
            return false;
        }
        _channel = channel; _isFd = true; IsConnected = true;
        return true;
    }

    public void Disconnect()
    {
        StopTrace();
        if (IsConnected)
        {
            PCANBasic.Uninitialize(_channel);
            IsConnected = false;
            _channel = PcanChannels.PCAN_NONEBUS;
        }
    }

    public void StartTrace()
    {
        if (!IsConnected || (_rxTask != null && !_rxTask.IsCompleted)) return;
        _sw.Restart();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _rxTask = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                TPCANStatus st;
                if (_isFd)
                {
                    st = PCANBasic.ReadFD(_channel, out var msg, out var ts);
                    if (st == TPCANStatus.PCAN_ERROR_OK) EmitFd(msg, ts);
                }
                else
                {
                    st = PCANBasic.Read(_channel, out var msg, out var ts);
                    if (st == TPCANStatus.PCAN_ERROR_OK) EmitClassic(msg, ts);
                }
                if (st == TPCANStatus.PCAN_ERROR_QRCVEMPTY) Thread.Sleep(1);
                else if (st != TPCANStatus.PCAN_ERROR_OK)
                    ErrorOccurred?.Invoke("PEAK Read: " + PCANBasic.ErrorText(st));
            }
        }, token);
    }

    public void StopTrace()
    {
        _cts?.Cancel();
        try { _rxTask?.Wait(500); } catch { }
        _cts?.Dispose(); _cts = null; _rxTask = null;
    }

    public bool Send(uint id, byte[] data, bool extended)
    {
        if (!IsConnected) return false;
        var msg = new TPCANMsg
        {
            ID = id,
            LEN = (byte)Math.Min(data.Length, 8),
            DATA = new byte[8],
            MSGTYPE = extended
                ? TPCANMessageType.PCAN_MESSAGE_EXTENDED
                : TPCANMessageType.PCAN_MESSAGE_STANDARD
        };
        Array.Copy(data, 0, msg.DATA, 0, Math.Min(data.Length, 8));
        var st = PCANBasic.Write(_channel, ref msg);
        if (st != TPCANStatus.PCAN_ERROR_OK)
        { ErrorOccurred?.Invoke("PEAK Write: " + PCANBasic.ErrorText(st)); return false; }
        EmitTx(id, data, extended, fd: false, brs: false);
        return true;
    }

    public bool SendFd(uint id, byte[] data, bool extended, bool brs)
    {
        if (!IsConnected) return false;
        var len = Math.Min(data.Length, 64);
        var msg = new TPCANMsgFD
        {
            ID = id,
            DLC = PCANBasic.LengthToDlc(len),
            DATA = new byte[64],
            MSGTYPE = TPCANMessageType.PCAN_MESSAGE_FD
                    | (extended ? TPCANMessageType.PCAN_MESSAGE_EXTENDED : 0)
                    | (brs ? TPCANMessageType.PCAN_MESSAGE_BRS : 0)
        };
        Array.Copy(data, 0, msg.DATA, 0, len);
        var st = PCANBasic.WriteFD(_channel, ref msg);
        if (st != TPCANStatus.PCAN_ERROR_OK)
        { ErrorOccurred?.Invoke("PEAK WriteFD: " + PCANBasic.ErrorText(st)); return false; }
        EmitTx(id, data, extended, fd: true, brs);
        return true;
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private static bool TryResolveChannel(string channelId, out ushort ch)
    {
        var field = typeof(PcanChannels).GetField(channelId);
        if (field is null) { ch = PcanChannels.PCAN_USBBUS1; return false; }
        ch = (ushort)field.GetValue(null)!;
        return true;
    }

    private static TPCANBaudrate ParseBaud(string s) => s switch
    {
        "1 Mbit/s" or "1000" or "1000000"        => TPCANBaudrate.PCAN_BAUD_1M,
        "800 kbit/s" or "800" or "800000"        => TPCANBaudrate.PCAN_BAUD_800K,
        "500 kbit/s" or "500" or "500000"        => TPCANBaudrate.PCAN_BAUD_500K,
        "250 kbit/s" or "250" or "250000"        => TPCANBaudrate.PCAN_BAUD_250K,
        "125 kbit/s" or "125" or "125000"        => TPCANBaudrate.PCAN_BAUD_125K,
        "100 kbit/s" or "100" or "100000"        => TPCANBaudrate.PCAN_BAUD_100K,
        "50 kbit/s"  or "50"  or "50000"         => TPCANBaudrate.PCAN_BAUD_50K,
        "20 kbit/s"  or "20"  or "20000"         => TPCANBaudrate.PCAN_BAUD_20K,
        "10 kbit/s"  or "10"  or "10000"         => TPCANBaudrate.PCAN_BAUD_10K,
        _ => TPCANBaudrate.PCAN_BAUD_500K
    };

    /// <summary>Build a PEAK CAN FD bitrate string from nominal+data kbit values.</summary>
    private static string BuildFdBitrate(string nominalKbit, string dataKbit)
    {
        // Default to common 500k/2M preset, but allow override of the data segment.
        // 80 MHz clock; tseg/sjw chosen for 70% sample point at 500k nominal / 2M data.
        // Users wanting custom timings can edit this string in code.
        var nom  = ParseKbit(nominalKbit, 500);
        var data = ParseKbit(dataKbit,    2000);
        var nomBrp = 80 / Math.Max(1u, nom * 1u / 1000u);   // crude; safe defaults below
        return nom switch
        {
            500 when data == 2000 =>
                "f_clock_mhz=80, nom_brp=2, nom_tseg1=63, nom_tseg2=16, nom_sjw=16, " +
                "data_brp=2, data_tseg1=15, data_tseg2=4, data_sjw=4",
            500 when data == 4000 =>
                "f_clock_mhz=80, nom_brp=2, nom_tseg1=63, nom_tseg2=16, nom_sjw=16, " +
                "data_brp=1, data_tseg1=15, data_tseg2=4, data_sjw=4",
            1000 when data == 4000 =>
                "f_clock_mhz=80, nom_brp=1, nom_tseg1=63, nom_tseg2=16, nom_sjw=16, " +
                "data_brp=1, data_tseg1=15, data_tseg2=4, data_sjw=4",
            _ =>
                // Fallback to 500k/2M if the combination isn't pre-tuned.
                "f_clock_mhz=80, nom_brp=2, nom_tseg1=63, nom_tseg2=16, nom_sjw=16, " +
                "data_brp=2, data_tseg1=15, data_tseg2=4, data_sjw=4"
        };
    }

    private static uint ParseKbit(string text, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var clean = new string(text.Where(c => char.IsDigit(c)).ToArray());
        if (uint.TryParse(clean, out var n))
            return n > 10000 ? n / 1000 : n;   // accept "500000" or "500"
        return fallback;
    }

    private void EmitClassic(TPCANMsg msg, TPCANTimestamp ts)
    {
        var time = ts.millis_overflow * 4294967.295 + ts.millis / 1000.0 + ts.micros / 1_000_000.0;
        var data = new byte[msg.LEN];
        Array.Copy(msg.DATA, 0, data, 0, msg.LEN);
        FrameReceived?.Invoke(new CanMessage
        {
            Time = time, Id = msg.ID,
            Direction = "Rx", EventType = "CAN Frame",
            Dlc = msg.LEN, DataLength = msg.LEN, Data = data,
            IsExtended = msg.MSGTYPE.HasFlag(TPCANMessageType.PCAN_MESSAGE_EXTENDED),
        });
    }

    private void EmitFd(TPCANMsgFD msg, ulong tsUs)
    {
        var time = tsUs / 1_000_000.0;
        var len  = PCANBasic.DlcToLength(msg.DLC);
        var data = new byte[len];
        Array.Copy(msg.DATA, 0, data, 0, len);
        var isFd = msg.MSGTYPE.HasFlag(TPCANMessageType.PCAN_MESSAGE_FD);
        FrameReceived?.Invoke(new CanMessage
        {
            Time = time, Id = msg.ID,
            Direction = "Rx", EventType = isFd ? "CAN FD Frame" : "CAN Frame",
            Dlc = msg.DLC, DataLength = len, Data = data,
            IsFd = isFd,
            IsBrs = msg.MSGTYPE.HasFlag(TPCANMessageType.PCAN_MESSAGE_BRS),
            IsExtended = msg.MSGTYPE.HasFlag(TPCANMessageType.PCAN_MESSAGE_EXTENDED),
        });
    }

    private void EmitTx(uint id, byte[] data, bool extended, bool fd, bool brs)
    {
        FrameReceived?.Invoke(new CanMessage
        {
            Time = _sw.Elapsed.TotalSeconds, Id = id,
            Direction = "Tx", EventType = fd ? "CAN FD Frame" : "CAN Frame",
            Dlc = fd ? PCANBasic.LengthToDlc(data.Length) : data.Length,
            DataLength = data.Length, Data = data,
            IsFd = fd, IsBrs = brs, IsExtended = extended,
        });
    }

    public void Dispose() => Disconnect();
}

/// <summary>PEAK PCAN channel handles used by the app.</summary>
public static class PcanChannels
{
    public const TPCANHandle PCAN_NONEBUS = 0x00;
    public const TPCANHandle PCAN_USBBUS1 = 0x51;
    public const TPCANHandle PCAN_USBBUS2 = 0x52;
    public const TPCANHandle PCAN_USBBUS3 = 0x53;
    public const TPCANHandle PCAN_USBBUS4 = 0x54;
    public const TPCANHandle PCAN_USBBUS5 = 0x55;
    public const TPCANHandle PCAN_USBBUS6 = 0x56;
    public const TPCANHandle PCAN_USBBUS7 = 0x57;
    public const TPCANHandle PCAN_USBBUS8 = 0x58;
    public const TPCANHandle PCAN_USBBUS9 = 0x509;
    public const TPCANHandle PCAN_USBBUS10 = 0x50A;
    public const TPCANHandle PCAN_USBBUS11 = 0x50B;
    public const TPCANHandle PCAN_USBBUS12 = 0x50C;
    public const TPCANHandle PCAN_USBBUS13 = 0x50D;
    public const TPCANHandle PCAN_USBBUS14 = 0x50E;
    public const TPCANHandle PCAN_USBBUS15 = 0x50F;
    public const TPCANHandle PCAN_USBBUS16 = 0x510;
}

public enum TPCANBaudrate : ushort
{
    PCAN_BAUD_1M   = 0x0014,
    PCAN_BAUD_800K = 0x0016,
    PCAN_BAUD_500K = 0x001C,
    PCAN_BAUD_250K = 0x011C,
    PCAN_BAUD_125K = 0x031C,
    PCAN_BAUD_100K = 0x432F,
    PCAN_BAUD_95K  = 0xC34E,
    PCAN_BAUD_83K  = 0x852B,
    PCAN_BAUD_50K  = 0x472F,
    PCAN_BAUD_47K  = 0x1414,
    PCAN_BAUD_33K  = 0x8B2F,
    PCAN_BAUD_20K  = 0x532F,
    PCAN_BAUD_10K  = 0x672F,
    PCAN_BAUD_5K   = 0x7F7F,
}

[Flags]
public enum TPCANMessageType : byte
{
    PCAN_MESSAGE_STANDARD = 0x00,
    PCAN_MESSAGE_RTR      = 0x01,
    PCAN_MESSAGE_EXTENDED = 0x02,
    PCAN_MESSAGE_FD       = 0x04,
    PCAN_MESSAGE_BRS      = 0x08,
    PCAN_MESSAGE_ESI      = 0x10,
    PCAN_MESSAGE_ECHO     = 0x20,
    PCAN_MESSAGE_ERRFRAME = 0x40,
    PCAN_MESSAGE_STATUS   = 0x80,
}

public enum TPCANStatus : uint
{
    PCAN_ERROR_OK           = 0x00000,
    PCAN_ERROR_XMTFULL      = 0x00001,
    PCAN_ERROR_OVERRUN      = 0x00002,
    PCAN_ERROR_BUSLIGHT     = 0x00004,
    PCAN_ERROR_BUSHEAVY     = 0x00008,
    PCAN_ERROR_BUSPASSIVE   = 0x40000,
    PCAN_ERROR_BUSOFF       = 0x00010,
    PCAN_ERROR_QRCVEMPTY    = 0x00020,
    PCAN_ERROR_QOVERRUN     = 0x00040,
    PCAN_ERROR_QXMTFULL     = 0x00080,
    PCAN_ERROR_REGTEST      = 0x00100,
    PCAN_ERROR_NODRIVER     = 0x00200,
    PCAN_ERROR_HWINUSE      = 0x00400,
    PCAN_ERROR_NETINUSE     = 0x00800,
    PCAN_ERROR_ILLHW        = 0x01400,
    PCAN_ERROR_ILLNET       = 0x01800,
    PCAN_ERROR_ILLCLIENT    = 0x01C00,
    PCAN_ERROR_RESOURCE     = 0x02000,
    PCAN_ERROR_ILLPARAMTYPE = 0x04000,
    PCAN_ERROR_ILLPARAMVAL  = 0x08000,
    PCAN_ERROR_UNKNOWN      = 0x10000,
    PCAN_ERROR_ILLDATA      = 0x20000,
    PCAN_ERROR_INITIALIZE   = 0x4000000,
}

public enum TPCANParameter : byte
{
    PCAN_ATTACHED_CHANNELS_COUNT = 0x2A,
    PCAN_ATTACHED_CHANNELS       = 0x2B,
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
public struct TPCANChannelInformation
{
    public TPCANHandle ChannelHandle;
    public byte ChannelType;
    public byte ControllerNumber;
    public uint DeviceFeatures;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
    public string DeviceName;
    public uint DeviceId;
    public uint ChannelCondition;
}

[StructLayout(LayoutKind.Sequential)]
public struct TPCANMsg
{
    public uint ID;
    public TPCANMessageType MSGTYPE;
    public byte LEN;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public byte[] DATA;
}

[StructLayout(LayoutKind.Sequential)]
public struct TPCANTimestamp
{
    public uint millis;
    public ushort millis_overflow;
    public ushort micros;
}

[StructLayout(LayoutKind.Sequential)]
public struct TPCANMsgFD
{
    public uint ID;
    public TPCANMessageType MSGTYPE;
    public byte DLC;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] DATA;
}

public static class PCANBasic
{
    private const string DLL = "PCANBasic.dll";

    [DllImport(DLL, EntryPoint = "CAN_Initialize")]
    public static extern TPCANStatus Initialize(
        TPCANHandle Channel,
        TPCANBaudrate Btr0Btr1,
        byte HwType = 0,
        uint IOPort = 0,
        ushort Interrupt = 0);

    [DllImport(DLL, EntryPoint = "CAN_InitializeFD")]
    public static extern TPCANStatus InitializeFD(
        TPCANHandle Channel,
        [MarshalAs(UnmanagedType.LPStr)] TPCANBitrateFD BitrateFD);

    [DllImport(DLL, EntryPoint = "CAN_Uninitialize")]
    public static extern TPCANStatus Uninitialize(TPCANHandle Channel);

    [DllImport(DLL, EntryPoint = "CAN_Reset")]
    public static extern TPCANStatus Reset(TPCANHandle Channel);

    [DllImport(DLL, EntryPoint = "CAN_GetStatus")]
    public static extern TPCANStatus GetStatus(TPCANHandle Channel);

    [DllImport(DLL, EntryPoint = "CAN_GetValue")]
    public static extern TPCANStatus GetValue(
        TPCANHandle Channel,
        TPCANParameter Parameter,
        out uint Buffer,
        uint BufferLength);

    [DllImport(DLL, EntryPoint = "CAN_GetValue")]
    public static extern TPCANStatus GetValue(
        TPCANHandle Channel,
        TPCANParameter Parameter,
        [Out] TPCANChannelInformation[] Buffer,
        uint BufferLength);

    [DllImport(DLL, EntryPoint = "CAN_Read")]
    public static extern TPCANStatus Read(
        TPCANHandle Channel,
        out TPCANMsg MessageBuffer,
        out TPCANTimestamp TimestampBuffer);

    [DllImport(DLL, EntryPoint = "CAN_ReadFD")]
    public static extern TPCANStatus ReadFD(
        TPCANHandle Channel,
        out TPCANMsgFD MessageBuffer,
        out TPCANTimestampFD TimestampBuffer);

    [DllImport(DLL, EntryPoint = "CAN_Write")]
    public static extern TPCANStatus Write(
        TPCANHandle Channel,
        ref TPCANMsg MessageBuffer);

    [DllImport(DLL, EntryPoint = "CAN_WriteFD")]
    public static extern TPCANStatus WriteFD(
        TPCANHandle Channel,
        ref TPCANMsgFD MessageBuffer);

    [DllImport(DLL, EntryPoint = "CAN_GetErrorText")]
    public static extern TPCANStatus GetErrorText(
        TPCANStatus Error,
        ushort Language,
        System.Text.StringBuilder StringBuffer);

    public static int DlcToLength(int dlc) => dlc switch
    {
        <= 8 => dlc,
        9  => 12,
        10 => 16,
        11 => 20,
        12 => 24,
        13 => 32,
        14 => 48,
        15 => 64,
        _  => 0
    };

    public static byte LengthToDlc(int length) => length switch
    {
        <= 8 => (byte)length,
        <= 12 => 9,
        <= 16 => 10,
        <= 20 => 11,
        <= 24 => 12,
        <= 32 => 13,
        <= 48 => 14,
        <= 64 => 15,
        _ => 15
    };

    public static string ErrorText(TPCANStatus status)
    {
        var sb = new System.Text.StringBuilder(256);
        GetErrorText(status, 0x09, sb);
        return sb.ToString();
    }
}
