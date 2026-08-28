
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using CanTracer.Models;

namespace CanTracer.ViewModels;

public sealed class SendSignalVm : INotifyPropertyChanged
{
    private readonly MtcSignal _signal;     // underlying testcase signal (edited in place)
    private readonly DbcSignal? _dbc;        // matching DBC signal (for value table)

    public SendSignalVm(MtcSignal signal, DbcSignal? dbc)
    {
        _signal = signal;
        _dbc = dbc;
        if (dbc != null && dbc.HasValueTable)
            Choices = new ObservableCollection<string>(dbc.ValueChoices());
    }

    public string Name => _signal.CanSignalName;

    /// <summary>Dropdown options when the signal has a value table; null otherwise.</summary>
    public ObservableCollection<string>? Choices { get; }

    public bool HasChoices => Choices != null && Choices.Count > 0;

    /// <summary>
    /// The raw text value as stored in the testcase. When the signal has a value
    /// table, the setter accepts either a bare number ("1") or a "1 (Fault)"
    /// string and stores just the number.
    /// </summary>
    public string Value
    {
        get
        {
            // If a value table exists, present "1 (Fault)" so the combo matches.
            if (_dbc != null && _dbc.HasValueTable
                && double.TryParse(_signal.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                return _dbc.FormatValue(v);
            return _signal.Value;
        }
        set
        {
            // Strip a trailing ": desc" if the user picked from the dropdown.
            var raw = value;
            var colon = raw.IndexOf(':');
            if (colon > 0) raw = raw.Substring(0, colon);
            _signal.Value = raw.Trim();
            Notify();
            Notify(nameof(DecodedLabel));
        }
    }

    /// <summary>Human-readable decode shown in a separate column.</summary>
    public string DecodedLabel
    {
        get
        {
            if (_dbc == null) return "(no DBC)";
            if (!double.TryParse(_signal.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                return "";
            return _dbc.FormatValue(v);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class SendMessageVm : INotifyPropertyChanged
{
    private readonly MtcMessage _msg;

    public SendMessageVm(MtcMessage msg, DbcMessage? dbcMsg, string busTag = "")
    {
        _msg = msg;
        BusTag = busTag;
        Transmitter = dbcMsg?.Transmitter ?? "";
        foreach (var s in msg.SignalItem)
        {
            DbcSignal? dbcSig = dbcMsg?.Signals.FirstOrDefault(
                d => d.Name == s.CanSignalName);
            Signals.Add(new SendSignalVm(s, dbcSig));
        }
    }

    public string ID   => _msg.ID;
    public string Name => _msg.Name;
    public string BusTag { get; }
    public string Transmitter { get; }

    /// <summary>Note column, e.g. "ID: 0x488 — From: XGW [INFO]".</summary>
    public string Note
    {
        get
        {
            var note = $"ID: {_msg.ID}";
            if (!string.IsNullOrEmpty(Transmitter)) note += $" — From: {Transmitter}";
            if (!string.IsNullOrEmpty(BusTag))      note += $" [{BusTag}]";
            return note;
        }
    }

    public int CycleTime
    {
        get => _msg.CycleTime;
        set { _msg.CycleTime = value; Notify(); }
    }

    public bool IsSelected { get; set; } = true;   // tick to include when firing

    public ObservableCollection<SendSignalVm> Signals { get; } = new();

    public MtcMessage Underlying => _msg;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
