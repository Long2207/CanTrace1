// ============================================================================
// AggregatedMessage.cs — one row per (Bus + ID) in the aggregated trace view.
// Tracks last data, cycle time (EMA), count, and a vertical list of decoded
// signals (DecodedRows). SignalValue is the row type for that decoded list.
// ============================================================================
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace CanTracer.Models;

public class SignalValue
{
    public string Name  { get; set; } = "";
    public string Value { get; set; } = "";
    public string Unit  { get; set; } = "";
}

// ---------- AggregatedMessage -------------------------------------------------

public class AggregatedMessage : INotifyPropertyChanged
{
    public string BusName    { get; set; } = "";
    public Brush? BusColor   { get; set; }
    public uint   Id         { get; set; }
    public bool   IsExtended { get; set; }
    public string EventType  { get; set; } = "CAN Frame";

    public string IdHex => IsExtended ? $"{Id:X}x" : $"{Id:X}";

    private string _name = "";
    public string Name { get => _name; set { _name = value; Notify(); } }

    private int _dlc;
    public int Dlc { get => _dlc; set { _dlc = value; Notify(); } }

    private int _dataLength;
    public int DataLength { get => _dataLength; set { _dataLength = value; Notify(); } }

    private byte[] _data = Array.Empty<byte>();
    public byte[] Data
    {
        get => _data;
        set { _data = value; Notify(); Notify(nameof(DataHex)); }
    }
    public string DataHex => _data.Length == 0
        ? ""
        : BitConverter.ToString(_data, 0, Math.Min(_data.Length, DataLength)).Replace('-', ' ');

    private int _count;
    public int Count { get => _count; set { _count = value; Notify(); } }

    private double _cycleMs;
    public double CycleMs
    {
        get => _cycleMs;
        set { _cycleMs = value; Notify(); Notify(nameof(CycleText)); }
    }
    public string CycleText => _cycleMs > 0 ? $"{_cycleMs:F1} ms" : "—";

    private string _direction = "Rx";
    public string Direction { get => _direction; set { _direction = value; Notify(); } }

    /// <summary>Vertical list of decoded signals (binding source for row details grid).</summary>
    public ObservableCollection<SignalValue> DecodedRows { get; } = new();

    public double LastTime { get; set; }

    private const double EmaAlpha = 0.2;

    public void Update(CanMessage f)
    {
        if (Count > 0)
        {
            var deltaMs = (f.Time - LastTime) * 1000.0;
            if (deltaMs > 0)
                CycleMs = _cycleMs <= 0 ? deltaMs : EmaAlpha * deltaMs + (1 - EmaAlpha) * _cycleMs;
        }
        Dlc        = f.Dlc;
        DataLength = f.DataLength;
        Data       = f.Data;
        Direction  = f.Direction;
        LastTime   = f.Time;
        Count++;

        if (f.DecodedSignalRows.Count > 0)
        {
            if (DecodedRows.Count == 0)
            {
                foreach (var sv in f.DecodedSignalRows) DecodedRows.Add(sv);
            }
            else if (DecodedRows.Count == f.DecodedSignalRows.Count)
            {
                for (int i = 0; i < DecodedRows.Count; i++)
                    DecodedRows[i].Value = f.DecodedSignalRows[i].Value;
            }
        }

        if (string.IsNullOrEmpty(_name) && !string.IsNullOrEmpty(f.Name)) Name = f.Name;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
