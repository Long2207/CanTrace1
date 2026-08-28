// ============================================================================
// VectorService.cs — DEBUG BUILD
// Adds verbose logging at every CAN FD critical point so failures show up in
// Visual Studio's Output window (View → Output → Show output from: Debug).
// Once everything works, you can remove the Debug.WriteLine calls.
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CanTracer.Models;
using vxlapi_NET;

namespace CanTracer.Services;

public sealed class VectorService : ICanService
{
    private readonly XLDriver _driver = new();
    private bool _driverOpened;
    private int  _portHandle = -1;
    private int  _eventHandle = -1;
    private ulong _accessMask;
    private bool _isFd;

    private CancellationTokenSource? _cts;
    private Task? _rxTask;
    private readonly Stopwatch _sw = new();

    public bool IsConnected { get; private set; }

    public event Action<CanMessage>? FrameReceived;
    public event Action<string>?     ErrorOccurred;

    private static void Log(string msg) => Debug.WriteLine("[VectorSvc] " + msg);

    public bool Connect(string channelId, string baud)
        => ConnectInternal(channelId, fd: false, baud, dataKbit: null);

    public bool ConnectFd(string channelId, string nominalKbit, string dataKbit)
        => ConnectInternal(channelId, fd: true, nominalKbit, dataKbit);

    private bool ConnectInternal(string channelId, bool fd, string nominalKbit, string? dataKbit)
    {
        Log($"ConnectInternal start, fd={fd}, ch='{channelId}', nom={nominalKbit}, data={dataKbit}");
        Disconnect();

        if (!TryParseId(channelId, out var hwType, out var hwIndex, out var hwChannel))
        {
            ErrorOccurred?.Invoke($"Vector: invalid channel id '{channelId}'");
            return false;
        }
        Log($"Parsed id → hwType={hwType}, hwIndex={hwIndex}, hwChannel={hwChannel}");

        var st = _driver.XL_OpenDriver();
        Log($"XL_OpenDriver → {st}");
        if (st != XLDefine.XL_Status.XL_SUCCESS)
        { ErrorOccurred?.Invoke("XL_OpenDriver: " + st); return false; }
        _driverOpened = true;

        const string appName = "CanTracer";
        var setApp = _driver.XL_SetApplConfig(appName, 0, (XLDefine.XL_HardwareType)hwType,
                                              (uint)hwIndex, (uint)hwChannel,
                                              XLDefine.XL_BusTypes.XL_BUS_TYPE_CAN);
        Log($"XL_SetApplConfig → {setApp}");

        var chMask = _driver.XL_GetChannelMask((XLDefine.XL_HardwareType)hwType, hwIndex, hwChannel);
        Log($"XL_GetChannelMask → 0x{chMask:X}");
        if (chMask == 0)
        { ErrorOccurred?.Invoke("Vector: XL_GetChannelMask returned 0"); return false; }
        _accessMask = chMask;
        ulong permissionMask = chMask;

        var rxQueueSize = (uint)(fd ? 16384 : 1024);
        var iface = fd ? XLDefine.XL_InterfaceVersion.XL_INTERFACE_VERSION_V4
                       : XLDefine.XL_InterfaceVersion.XL_INTERFACE_VERSION;

        st = _driver.XL_OpenPort(ref _portHandle, appName, _accessMask, ref permissionMask,
                                 rxQueueSize, iface, XLDefine.XL_BusTypes.XL_BUS_TYPE_CAN);
        Log($"XL_OpenPort → status={st}, portHandle={_portHandle}, permission=0x{permissionMask:X}");
        if (st != XLDefine.XL_Status.XL_SUCCESS || _portHandle == -1)
        { ErrorOccurred?.Invoke("Vector: XL_OpenPort failed: " + st); return false; }

        // Open acceptance filter fully (accept all STD + EXT ids). The Vector
        // driver can retain a restrictive filter from a previous session (e.g.
        // CANoe just closed), which would silently drop all frames.
        _driver.XL_CanSetChannelAcceptance(_portHandle, _accessMask, 0x0, 0x0,
                                           XLDefine.XL_AcceptanceFilter.XL_CAN_STD);
        _driver.XL_CanSetChannelAcceptance(_portHandle, _accessMask, 0x0, 0x0,
                                           XLDefine.XL_AcceptanceFilter.XL_CAN_EXT);
        Log("Acceptance filter opened (STD + EXT, accept all)");

        if (fd)
        {
            var nomBps  = ParseKbit(nominalKbit, 500_000);
            var dataBps = ParseKbit(dataKbit ?? "2000", 2_000_000);
            var conf = new XLClass.XLcanFdConf
            {
                arbitrationBitRate = nomBps,
                // Match CANoe config: 500 kbit/s @ 80 MHz, BRP=2 → 80 tq, SP=70%
                tseg1Abr = 55, tseg2Abr = 24, sjwAbr = 24,
                dataBitRate        = dataBps,
                // Match CANoe config: 2 Mbit/s @ 80 MHz, BRP=1 → 40 tq, SP=75%
                tseg1Dbr = 29, tseg2Dbr = 10, sjwDbr = 10,
                options  = 0,   // ISO mode. If ECU uses non-ISO, change to:
                                //   (byte)XLDefine.XL_CANFD_ConfigOptions.XL_CANFD_CONFOPT_NO_ISO
            };
            Log($"FD config: arb={nomBps}, data={dataBps}, " +
                $"tseg1Abr={conf.tseg1Abr}, tseg2Abr={conf.tseg2Abr}, sjwAbr={conf.sjwAbr}, " +
                $"tseg1Dbr={conf.tseg1Dbr}, tseg2Dbr={conf.tseg2Dbr}, sjwDbr={conf.sjwDbr}, " +
                $"options={conf.options}");
            st = _driver.XL_CanFdSetConfiguration(_portHandle, _accessMask, conf);
            Log($"XL_CanFdSetConfiguration → {st}");
            if (st != XLDefine.XL_Status.XL_SUCCESS)
            { ErrorOccurred?.Invoke("XL_CanFdSetConfiguration: " + st); Disconnect(); return false; }
        }
        else
        {
            var bps = ParseKbit(nominalKbit, 500_000);
            st = _driver.XL_CanSetChannelBitrate(_portHandle, _accessMask, bps);
            Log($"XL_CanSetChannelBitrate({bps}) → {st}");
            if (st != XLDefine.XL_Status.XL_SUCCESS)
            { ErrorOccurred?.Invoke("XL_CanSetChannelBitrate: " + st); Disconnect(); return false; }
        }

        st = _driver.XL_SetNotification(_portHandle, ref _eventHandle, 1);
        Log($"XL_SetNotification → {st}, eventHandle={_eventHandle}");
        if (st != XLDefine.XL_Status.XL_SUCCESS)
        { ErrorOccurred?.Invoke("XL_SetNotification: " + st); Disconnect(); return false; }

        st = _driver.XL_ActivateChannel(_portHandle, _accessMask,
                                        XLDefine.XL_BusTypes.XL_BUS_TYPE_CAN,
                                        XLDefine.XL_AC_Flags.XL_ACTIVATE_RESET_CLOCK);
        Log($"XL_ActivateChannel → {st}");
        if (st != XLDefine.XL_Status.XL_SUCCESS)
        { ErrorOccurred?.Invoke("XL_ActivateChannel: " + st); Disconnect(); return false; }

        // Request chip state — logged in the RX loop so we can confirm the bus is alive.
        _driver.XL_CanRequestChipState(_portHandle, _accessMask);

        _isFd = fd;
        IsConnected = true;
        Log($"Connect SUCCESS, isFd={_isFd}");
        return true;
    }

    public void Disconnect()
    {
        StopTrace();
        if (_portHandle != -1)
        {
            try { _driver.XL_DeactivateChannel(_portHandle, _accessMask); _driver.XL_ClosePort(_portHandle); } catch { }
            _portHandle = -1;
        }
        if (_driverOpened) { try { _driver.XL_CloseDriver(); } catch { } _driverOpened = false; }
        IsConnected = false;
    }

    public void StartTrace()
    {
        if (!IsConnected || (_rxTask != null && !_rxTask.IsCompleted)) return;
        _sw.Restart();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var fd = _isFd;
        Log($"StartTrace → spawning RX task, fd={fd}");
        _rxTask = Task.Run(() =>
        {
            try
            {
                if (fd) RunRxLoopFd(token);
                else    RunRxLoopClassic(token);
            }
            catch (Exception ex) { Log("RX task crashed: " + ex); }
            Log("RX task exited");
        }, token);
    }

    public void StopTrace()
    {
        _cts?.Cancel();
        try { _rxTask?.Wait(500); } catch { }
        _cts?.Dispose(); _cts = null;
        _rxTask = null;
    }

    private void RunRxLoopClassic(CancellationToken token)
    {
        var ev = new XLClass.xl_event();
        int frameCount = 0, otherTagCount = 0, emptyPolls = 0;
        var statsTimer = Stopwatch.StartNew();
        Log("Classic RX loop started (polling mode)");

        while (!token.IsCancellationRequested)
        {
            var st = _driver.XL_Receive(_portHandle, ref ev);

            if (st != XLDefine.XL_Status.XL_SUCCESS)
            {
                // Queue empty (or transient) → brief sleep, keep polling.
                emptyPolls++;
                if (statsTimer.ElapsedMilliseconds > 2000)
                {
                    Log($"Classic RX stats: frames={frameCount}, other_tags={otherTagCount}, empty_polls={emptyPolls}, lastStatus={st}");
                    statsTimer.Restart();
                }
                Thread.Sleep(1);
                continue;
            }

            // Got an event. Log the tag of the first few so we know what's arriving.
            if (frameCount + otherTagCount < 5) Log($"Classic event tag={ev.tag}");

            if (ev.tag != XLDefine.XL_EventTags.XL_RECEIVE_MSG)
            {
                otherTagCount++;
                continue;
            }

            var flags = ev.tagData.can_Msg.flags;
            if (flags.HasFlag(XLDefine.XL_MessageFlags.XL_CAN_MSG_FLAG_ERROR_FRAME)
                || flags.HasFlag(XLDefine.XL_MessageFlags.XL_CAN_MSG_FLAG_REMOTE_FRAME)
                || flags.HasFlag(XLDefine.XL_MessageFlags.XL_CAN_MSG_FLAG_TX_COMPLETED))
                continue;

            var dlc = (int)ev.tagData.can_Msg.dlc;
            var data = new byte[dlc];
            Buffer.BlockCopy(ev.tagData.can_Msg.data, 0, data, 0, dlc);

            if (frameCount < 3) Log($"Classic RX #{frameCount}: id=0x{ev.tagData.can_Msg.id:X}, dlc={dlc}");
            frameCount++;

            FrameReceived?.Invoke(new CanMessage
            {
                Time       = ev.timeStamp / 1_000_000_000.0,
                Id         = ev.tagData.can_Msg.id & 0x1FFFFFFF,
                IsExtended = (ev.tagData.can_Msg.id & 0x80000000) != 0,
                Direction  = "Rx", EventType = "CAN Frame",
                Dlc = dlc, DataLength = dlc, Data = data,
            });
        }
    }

    private void RunRxLoopFd(CancellationToken token)
    {
        var ev = new XLClass.XLcanRxEvent();
        int frameCount = 0, skippedTagCount = 0, emptyPolls = 0;
        var statsTimer = Stopwatch.StartNew();
        Log("FD RX loop started (polling mode)");

        while (!token.IsCancellationRequested)
        {
            var st = _driver.XL_CanReceive(_portHandle, ref ev);

            if (st != XLDefine.XL_Status.XL_SUCCESS)
            {
                emptyPolls++;
                if (statsTimer.ElapsedMilliseconds > 2000)
                {
                    Log($"FD RX stats: frames={frameCount}, skipped_tags={skippedTagCount}, empty_polls={emptyPolls}, lastStatus={st}");
                    statsTimer.Restart();
                }
                Thread.Sleep(1);
                continue;
            }

            if (frameCount + skippedTagCount < 5) Log($"FD event tag={ev.tag}");

            if (ev.tag == XLDefine.XL_CANFD_RX_EventTags.XL_CAN_EV_TAG_CHIP_STATE)
            {
                skippedTagCount++;
                continue;
            }

            var isRx = ev.tag == XLDefine.XL_CANFD_RX_EventTags.XL_CAN_EV_TAG_RX_OK;
            if (!isRx)
            {
                skippedTagCount++;
                continue;
            }

            uint   canId   = ev.tagData.canRxOkMsg.canId;
            int    dlcCode = (int)ev.tagData.canRxOkMsg.dlc;
            byte[] raw     = ev.tagData.canRxOkMsg.data;

            var dataLen = DlcCodeToLength(dlcCode);
            var data = new byte[dataLen];
            Buffer.BlockCopy(raw, 0, data, 0, dataLen);

            if (frameCount < 3) Log($"FD RX #{frameCount}: id=0x{canId:X}, dlcCode={dlcCode}, len={dataLen}");
            frameCount++;

            FrameReceived?.Invoke(new CanMessage
            {
                Time       = ev.timeStamp / 1_000_000_000.0,
                Id         = canId & 0x1FFFFFFF,
                IsExtended = (canId & 0x80000000) != 0,
                Direction  = "Rx",
                EventType  = "CAN FD Frame",
                Dlc = dlcCode, DataLength = dataLen, Data = data, IsFd = true,
            });
        }
    }

    public bool Send(uint id, byte[] data, bool extended)
    {
        if (!IsConnected) return false;
        var col = new XLClass.xl_event_collection(1);
        col.xlEvent[0].tag = XLDefine.XL_EventTags.XL_TRANSMIT_MSG;
        col.xlEvent[0].tagData.can_Msg.id  = extended ? (id | 0x80000000) : id;
        col.xlEvent[0].tagData.can_Msg.dlc = (ushort)Math.Min(data.Length, 8);
        col.xlEvent[0].tagData.can_Msg.flags = 0;
        for (int i = 0; i < Math.Min(data.Length, 8); i++)
            col.xlEvent[0].tagData.can_Msg.data[i] = data[i];
        var st = _driver.XL_CanTransmit(_portHandle, _accessMask, col);
        if (st != XLDefine.XL_Status.XL_SUCCESS)
        { ErrorOccurred?.Invoke("XL_CanTransmit: " + st); return false; }
        EmitTx(id, data, extended, fd: false, brs: false);
        return true;
    }

    public bool SendFd(uint id, byte[] data, bool extended, bool brs)
    {
        if (!IsConnected) return false;
        var len = Math.Min(data.Length, 64);
        var col = new XLClass.xl_canfd_event_collection(1);
        col.xlCANFDEvent[0].tag = XLDefine.XL_CANFD_TX_EventTags.XL_CAN_EV_TAG_TX_MSG;
        col.xlCANFDEvent[0].tagData.canId = extended ? (id | 0x80000000) : id;
        col.xlCANFDEvent[0].tagData.dlc   = LengthToDlcEnum(len);
        col.xlCANFDEvent[0].tagData.msgFlags =
            XLDefine.XL_CANFD_TX_MessageFlags.XL_CAN_TXMSG_FLAG_EDL
            | (brs ? XLDefine.XL_CANFD_TX_MessageFlags.XL_CAN_TXMSG_FLAG_BRS : 0);
        for (int i = 0; i < len; i++) col.xlCANFDEvent[0].tagData.data[i] = data[i];
        uint sent = 0;
        var st = _driver.XL_CanTransmitEx(_portHandle, _accessMask, ref sent, col);
        if (st != XLDefine.XL_Status.XL_SUCCESS)
        { ErrorOccurred?.Invoke("XL_CanTransmitEx: " + st); return false; }
        EmitTx(id, data, extended, fd: true, brs);
        return true;
    }

    private void EmitTx(uint id, byte[] data, bool extended, bool fd, bool brs)
    {
        FrameReceived?.Invoke(new CanMessage
        {
            Time = _sw.Elapsed.TotalSeconds, Id = id,
            Direction = "Tx", EventType = fd ? "CAN FD Frame" : "CAN Frame",
            Dlc = fd ? LengthToDlcCode(data.Length) : data.Length,
            DataLength = data.Length, Data = data,
            IsFd = fd, IsBrs = brs, IsExtended = extended,
        });
    }

    private static bool TryParseId(string id, out int hwType, out int hwIndex, out int hwChannel)
    {
        hwType = hwIndex = hwChannel = 0;
        if (!id.StartsWith("VECTOR:")) return false;
        var parts = id.Split(':');
        return parts.Length == 4
            && int.TryParse(parts[1], out hwType)
            && int.TryParse(parts[2], out hwIndex)
            && int.TryParse(parts[3], out hwChannel);
    }

    private static uint ParseKbit(string text, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var clean = new string(text.Where(c => char.IsDigit(c) || c == '.').ToArray());
        if (uint.TryParse(clean, out var n))
            return n > 10000 ? n : n * 1000;
        return fallback;
    }

    private static int DlcCodeToLength(int code) => code switch
    {
        <= 8 => code,
        9 => 12, 10 => 16, 11 => 20, 12 => 24,
        13 => 32, 14 => 48, 15 => 64,
        _ => 0
    };
    private static int LengthToDlcCode(int len) => len switch
    {
        <= 8 => len,
        <= 12 => 9, <= 16 => 10, <= 20 => 11,
        <= 24 => 12, <= 32 => 13, <= 48 => 14, _ => 15
    };
    private static XLDefine.XL_CANFD_DLC LengthToDlcEnum(int len)
        => (XLDefine.XL_CANFD_DLC)LengthToDlcCode(len);

    public void Dispose() => Disconnect();
}
