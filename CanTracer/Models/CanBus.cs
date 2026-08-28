// ============================================================================
// CanBus.cs — one configured CAN/CANFD bus (channel + DBC + runtime state).
// Exposes an ICanService (PcanService or VectorService) created by BusManager.
// ============================================================================
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using CanTracer.Services;

namespace CanTracer.Models;

public class CanBus : INotifyPropertyChanged
{
    public string Name        { get; set; } = "BUS";

    /// <summary>Channel id from ChannelDiscovery (e.g. "PCAN_USBBUS1" or "VECTOR:1:0:0").</summary>
    public string ChannelId   { get; set; } = "PCAN_USBBUS1";
    /// <summary>Friendly label shown in the bus card and Settings grid.</summary>
    public string ChannelLabel { get; set; } = "PEAK PCAN-USB #1";

    public bool   IsFd        { get; set; }
    public string Baud        { get; set; } = "500 kbit/s";
    public string FdNominal   { get; set; } = "500";
    public string FdData      { get; set; } = "2000";

    public string ColorHex   { get; set; } = "#FF808080";
    public Brush  ColorBrush { get; set; } = Brushes.Gray;

    /// <summary>The actual hardware service (created by BusManager.Add).</summary>
    internal ICanService? Service { get; set; }

    internal Dictionary<uint, DbcMessage> Dbc { get; set; } = new();

    private string _dbcPath = "";
    public string DbcPath
    {
        get => _dbcPath;
        set { _dbcPath = value; Notify(); Notify(nameof(DbcInfo)); }
    }
    public string DbcInfo => string.IsNullOrEmpty(DbcPath)
        ? "No DBC"
        : $"DBC: {Path.GetFileName(DbcPath)} ({Dbc.Count})";

    public string Info => IsFd
        ? $"{ChannelLabel} · CAN FD"
        : $"{ChannelLabel} · {Baud}";

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; Notify(); Notify(nameof(StatusText)); }
    }
    private bool _isTracing;
    public bool IsTracing
    {
        get => _isTracing;
        set { _isTracing = value; Notify(); Notify(nameof(StatusText)); }
    }
    private int _frameCount;
    public int FrameCount { get => _frameCount; set { _frameCount = value; Notify(); } }

    public string StatusText => IsTracing ? "TRACE" : (IsConnected ? "READY" : "OFF");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
