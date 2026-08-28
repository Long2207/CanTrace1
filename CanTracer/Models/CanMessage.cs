// ============================================================================
// CanMessage.cs — a single CAN/CANFD frame (Rx or Tx) shown in the trace.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace CanTracer.Models;

public class CanMessage
{
    public double Time { get; set; }
    public uint   Id   { get; set; }
    public string Name { get; set; } = "";

    /// <summary>ID as hex; extended frames get an "x" suffix (e.g. "1ABCDEFx").</summary>
    public string IdHex => IsExtended ? $"{Id:X}x" : $"{Id:X}";

    public string Direction { get; set; } = "Rx";
    public string EventType { get; set; } = "CAN Frame";
    public int    Dlc        { get; set; }
    public int    DataLength { get; set; }
    public byte[] Data       { get; set; } = Array.Empty<byte>();

    /// <summary>Payload as space-separated hex bytes (e.g. "01 A2 FF").</summary>
    public string DataHex => Data is null || Data.Length == 0
        ? ""
        : string.Join(" ", System.Array.ConvertAll(Data, b => b.ToString("X2")));

    public bool   IsFd       { get; set; }
    public bool   IsBrs      { get; set; }
    public bool   IsExtended { get; set; }

    public string BusName  { get; set; } = "";
    public Brush? BusColor { get; set; }

    /// <summary>Decoded signals as a single text line — legacy / tooltip use.</summary>
    public string DecodedSignals { get; set; } = "";

    /// <summary>Decoded signals as discrete rows for the vertical details grid.</summary>
    public List<SignalValue> DecodedSignalRows { get; set; } = new();
}
