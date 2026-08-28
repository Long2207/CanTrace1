// ============================================================================
// BusManager.cs (v6)
// Same role as before, but now creates the appropriate ICanService based on
// the channel id prefix:
//   - "PCAN_*"   → PcanService
//   - "VECTOR:*" → VectorService
// ============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Media;
using CanTracer.Models;
using vxlapi_NET;

namespace CanTracer.Services;

public enum CanSupplier { Peak, Vector }

public sealed class ChannelInfo
{
    public CanSupplier Supplier   { get; init; }
    /// <summary>Opaque ID consumed by ICanService.Connect, e.g. "PCAN_USBBUS1" or "VECTOR:0:1".</summary>
    public string      Id         { get; init; } = "";
    /// <summary>Human-readable label for the dropdown, e.g. "PEAK PCAN-USB #1" or "Vector VN1640A Ch1".</summary>
    public string      Label      { get; init; } = "";
    public bool        SupportsFd { get; init; }

    public override string ToString() => Label;
}

public static class ChannelDiscovery
{
    /// <summary>Scan both vendors and return every detected channel.</summary>
    public static List<ChannelInfo> Discover()
    {
        var list = new List<ChannelInfo>();
        try { list.AddRange(DiscoverPeak()); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("PEAK discovery failed: " + ex);
        }
        try { list.AddRange(DiscoverVector()); } catch { /* Vector driver missing -> silently skip */ }
        return list;
    }

    private static IEnumerable<ChannelInfo> DiscoverPeak()
    {
        var st = PCANBasic.GetValue(
            PcanChannels.PCAN_NONEBUS,
            TPCANParameter.PCAN_ATTACHED_CHANNELS_COUNT,
            out var count,
            sizeof(uint));
        if (st != TPCANStatus.PCAN_ERROR_OK || count == 0)
            yield break;

        var channels = new TPCANChannelInformation[count];
        st = PCANBasic.GetValue(
            PcanChannels.PCAN_NONEBUS,
            TPCANParameter.PCAN_ATTACHED_CHANNELS,
            channels,
            (uint)(channels.Length * System.Runtime.InteropServices.Marshal.SizeOf<TPCANChannelInformation>()));
        if (st != TPCANStatus.PCAN_ERROR_OK)
            yield break;

        foreach (var ch in channels)
        {
            var id = PeakHandleToId(ch.ChannelHandle);
            if (string.IsNullOrEmpty(id))
                continue;

            var statusText = ch.ChannelCondition switch
            {
                0x01 => "",
                0x02 => " (occupied)",
                0x03 => " (PCAN-View)",
                _    => $" (condition 0x{ch.ChannelCondition:X})",
            };

            var channelNumber = ch.ControllerNumber + 1;
            yield return new ChannelInfo
            {
                Supplier   = CanSupplier.Peak,
                Id         = id,
                Label      = $"PEAK {ch.DeviceName} Ch{channelNumber} ({id}){statusText}",
                SupportsFd = (ch.DeviceFeatures & 0x01) != 0,
            };
        }
    }

    private static string PeakHandleToId(ushort handle) => handle switch
    {
        PcanChannels.PCAN_USBBUS1  => "PCAN_USBBUS1",
        PcanChannels.PCAN_USBBUS2  => "PCAN_USBBUS2",
        PcanChannels.PCAN_USBBUS3  => "PCAN_USBBUS3",
        PcanChannels.PCAN_USBBUS4  => "PCAN_USBBUS4",
        PcanChannels.PCAN_USBBUS5  => "PCAN_USBBUS5",
        PcanChannels.PCAN_USBBUS6  => "PCAN_USBBUS6",
        PcanChannels.PCAN_USBBUS7  => "PCAN_USBBUS7",
        PcanChannels.PCAN_USBBUS8  => "PCAN_USBBUS8",
        PcanChannels.PCAN_USBBUS9  => "PCAN_USBBUS9",
        PcanChannels.PCAN_USBBUS10 => "PCAN_USBBUS10",
        PcanChannels.PCAN_USBBUS11 => "PCAN_USBBUS11",
        PcanChannels.PCAN_USBBUS12 => "PCAN_USBBUS12",
        PcanChannels.PCAN_USBBUS13 => "PCAN_USBBUS13",
        PcanChannels.PCAN_USBBUS14 => "PCAN_USBBUS14",
        PcanChannels.PCAN_USBBUS15 => "PCAN_USBBUS15",
        PcanChannels.PCAN_USBBUS16 => "PCAN_USBBUS16",
        _ => "",
    };

    private static IEnumerable<ChannelInfo> DiscoverVector()
    {
        var driver = new XLDriver();
        var open = driver.XL_OpenDriver();
        if (open != XLDefine.XL_Status.XL_SUCCESS) yield break;

        var cfg = new XLClass.xl_driver_config();
        var st = driver.XL_GetDriverConfig(ref cfg);
        if (st != XLDefine.XL_Status.XL_SUCCESS || cfg.channelCount == 0)
        {
            driver.XL_CloseDriver();
            yield break;
        }

        for (int i = 0; i < cfg.channelCount; i++)
        {
            var ch = cfg.channel[i];
            var rawName = ch.name?.ToString() ?? "";
            var trans   = ch.transceiverName?.ToString() ?? "";

            if (rawName.Contains("Virtual")
                || trans.Contains("On board D/A")
                || trans.Contains("LIN")
                || trans.Contains("Unknown Transceiver"))
                continue;

            var nullIdx = rawName.IndexOf('\0');
            var hwName = nullIdx >= 0 ? rawName.Substring(0, nullIdx) : rawName;
            if (string.IsNullOrWhiteSpace(hwName)) continue;

            yield return new ChannelInfo
            {
                Supplier   = CanSupplier.Vector,
                Id         = $"VECTOR:{(int)ch.hwType}:{ch.hwIndex}:{ch.hwChannel}",
                Label      = $"Vector {hwName} (idx {ch.hwIndex}, ch {ch.hwChannel + 1}) - {trans}",
                SupportsFd = trans.IndexOf("CAN FD", StringComparison.OrdinalIgnoreCase) >= 0
                            || trans.IndexOf("CANFD",  StringComparison.OrdinalIgnoreCase) >= 0
                            || true,
            };
        }
        driver.XL_CloseDriver();
    }
}

public interface ICanService : IDisposable
{
    bool IsConnected { get; }

    /// <summary>Connect classical CAN at the given bitrate (e.g. "500 kbit/s").</summary>
    bool Connect(string channelId, string baud);

    /// <summary>Connect CAN FD with arbitration + data bitrates (e.g. "500", "2000" kbit/s).</summary>
    bool ConnectFd(string channelId, string nominalKbit, string dataKbit);

    void Disconnect();

    void StartTrace();
    void StopTrace();

    bool Send(uint id, byte[] data, bool extended);
    bool SendFd(uint id, byte[] data, bool extended, bool brs);

    event Action<CanMessage>? FrameReceived;
    event Action<string>?     ErrorOccurred;
}

public class BusManager : IDisposable
{
    private static readonly string[] Palette =
    {
        "#FF1976D2", "#FFD32F2F", "#FF388E3C", "#FFF57C00", "#FF7B1FA2",
        "#FF00838F", "#FF5D4037", "#FF455A64", "#FFC2185B", "#FF689F38",
    };

    public ObservableCollection<CanBus> Buses { get; } = new();

    public event Action<CanMessage>? FrameReceived;
    public event Action<string>?     ErrorOccurred;

    public void Add(CanBus bus)
    {
        // 1) Auto-assign color if needed.
        if (bus.ColorBrush == Brushes.Gray)
        {
            bus.ColorHex = Palette[Buses.Count % Palette.Length];
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(bus.ColorHex)!;
            b.Freeze();
            bus.ColorBrush = b;
        }

        // 2) Pick the right service implementation.
        bus.Service = CreateService(bus.ChannelId);
        bus.Service.FrameReceived += f => OnFrame(bus, f);
        bus.Service.ErrorOccurred += msg => ErrorOccurred?.Invoke($"[{bus.Name}] {msg}");

        Buses.Add(bus);
    }

    private static ICanService CreateService(string channelId)
    {
        if (channelId.StartsWith("VECTOR:")) return new VectorService();
        return new PcanService();   // default
    }

    public void Remove(CanBus bus)
    {
        bus.Service?.Disconnect();
        bus.Service?.Dispose();
        Buses.Remove(bus);
    }

    public bool Connect(CanBus bus)
    {
        if (bus.Service is null) return false;
        bus.IsConnected = bus.IsFd
            ? bus.Service.ConnectFd(bus.ChannelId, bus.FdNominal, bus.FdData)
            : bus.Service.Connect(bus.ChannelId, bus.Baud);
        return bus.IsConnected;
    }

    public void Disconnect(CanBus bus)
    {
        bus.Service?.Disconnect();
        bus.IsConnected = false;
        bus.IsTracing = false;
    }

    public void StartTrace(CanBus bus)
    {
        if (!bus.IsConnected || bus.Service is null) return;
        bus.Service.StartTrace();
        bus.IsTracing = true;
    }

    public void StopTrace(CanBus bus)
    {
        bus.Service?.StopTrace();
        bus.IsTracing = false;
    }

    public void LoadDbc(CanBus bus, string path)
    {
        bus.Dbc = DbcParser.Parse(path);
        bus.DbcPath = path;
    }

    public bool Send(CanBus bus, uint id, byte[] data, bool extended, bool fd, bool brs)
    {
        if (bus.Service is null) return false;
        return fd ? bus.Service.SendFd(id, data, extended, brs)
                  : bus.Service.Send(id, data, extended);
    }

    // -------------------------------------------------------------------------
    // Frame handling (same as v5 — tag + DBC decode + forward).
    // -------------------------------------------------------------------------
    private void OnFrame(CanBus bus, CanMessage f)
    {
        f.BusName  = bus.Name;
        f.BusColor = bus.ColorBrush;

        if (bus.Dbc.TryGetValue(f.Id, out var dbcMsg))
        {
            f.Name = dbcMsg.Name;
            if (dbcMsg.Signals.Count > 0)
            {
                var sb = new StringBuilder();
                var rows = new List<SignalValue>(dbcMsg.Signals.Count);
                foreach (var s in dbcMsg.Signals)
                {
                    var v = s.Decode(f.Data);
                    var valStr = v.ToString("G6", CultureInfo.InvariantCulture);
                    sb.Append(s.Name).Append('=').Append(valStr);
                    if (!string.IsNullOrEmpty(s.Unit)) sb.Append(' ').Append(s.Unit);
                    sb.Append("  ");
                    rows.Add(new SignalValue { Name = s.Name, Value = valStr, Unit = s.Unit });
                }
                f.DecodedSignals = sb.ToString();
                f.DecodedSignalRows = rows;
            }
        }
        FrameReceived?.Invoke(f);
    }

    public void Dispose()
    {
        foreach (var b in Buses) b.Service?.Dispose();
        Buses.Clear();
    }
}
