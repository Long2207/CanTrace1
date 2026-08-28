// ============================================================================
// SendByDbcDialog.xaml.cs
// Lets the user pick a DBC message, fill in physical values for each signal,
// and send the result one-shot or as a cyclic job. Live encoded-bytes preview
// updates as the user types.
// ============================================================================
using CanTracer.Models;
using CanTracer.Services;
using CanTracer.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CanTracer.Views;

public partial class SendByDbcDialog : Window
{
    private readonly MainViewModel _vm;

    /// <summary>Row binding for the signal editor list.</summary>
    public sealed class SigRow : INotifyPropertyChanged
    {
        public DbcSignal Signal { get; }
        public string Name  => Signal.Name;
        public string Unit  => Signal.Unit;
        public string Range => (Signal.Min == 0 && Signal.Max == 0)
            ? $"len={Signal.Length} bits"
            : $"[{Signal.Min:G6} … {Signal.Max:G6}]";

        private string _valueText = "0";
        public string ValueText
        {
            get => _valueText;
            set { _valueText = value; OnChanged(); ValueChanged?.Invoke(); }
        }

        public event Action? ValueChanged;

        public SigRow(DbcSignal sig) { Signal = sig; }

        public bool TryParseValue(out double v)
            => double.TryParse(_valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private readonly ObservableCollection<SigRow> _signals = new();
    private List<DbcMessage> _filteredMessages = new();

    public SendByDbcDialog(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        SignalItems.ItemsSource = _signals;

        var connected = _vm.Buses.Where(b => b.IsConnected).ToList();
        BusBox.ItemsSource = connected;
        if (connected.Count > 0) BusBox.SelectedIndex = 0;
        else SetStatus("No bus is connected.", false);
    }

    // ----- bus changed -> rebuild message list ---------------------------------

    private void OnBusChanged(object sender, SelectionChangedEventArgs e) => RebuildMessageList();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => RebuildMessageList();

    private void RebuildMessageList()
    {
        _signals.Clear();
        UpdatePreview();
        if (BusBox.SelectedItem is not CanBus bus) { MsgBox.ItemsSource = null; return; }
        var dbc = _vm.GetDbcFor(bus);
        if (dbc is null)
        {
            MsgBox.ItemsSource = null;
            SetStatus($"Bus '{bus.Name}' has no DBC loaded.", false);
            return;
        }

        var q = (SearchBox?.Text ?? "").Trim();
        _filteredMessages = dbc.Values
            .Where(m => string.IsNullOrEmpty(q)
                        || m.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || m.Id.ToString("X").Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Name)
            .ToList();

        MsgBox.ItemsSource = _filteredMessages.Select(m => $"{m.Name}  (0x{m.Id:X}, {m.Signals.Count} signals)").ToList();
        if (_filteredMessages.Count > 0) MsgBox.SelectedIndex = 0;
        SetStatus("", true);
    }

    // ----- message changed -> rebuild signal editor ----------------------------

    private void OnMessageChanged(object sender, SelectionChangedEventArgs e)
    {
        _signals.Clear();
        if (MsgBox.SelectedIndex < 0 || MsgBox.SelectedIndex >= _filteredMessages.Count) return;
        var msg = _filteredMessages[MsgBox.SelectedIndex];
        foreach (var s in msg.Signals)
        {
            var row = new SigRow(s);
            row.ValueChanged += UpdatePreview;
            _signals.Add(row);
        }
        UpdatePreview();
    }

    // ----- live encode preview -------------------------------------------------

    private void UpdatePreview()
    {
        if (PreviewBlock is null) return;
        var msg = SelectedMessage();
        if (msg is null) { PreviewBlock.Text = ""; return; }
        try
        {
            var bytes = EncodeCurrent(msg);
            PreviewBlock.Text = $"ID=0x{msg.Id:X}  DLC={msg.Dlc}  Data=" +
                                BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, msg.Dlc)).Replace('-', ' ');
        }
        catch (Exception ex)
        {
            PreviewBlock.Text = $"<encode error: {ex.Message}>";
        }
    }

    private DbcMessage? SelectedMessage()
        => (MsgBox.SelectedIndex >= 0 && MsgBox.SelectedIndex < _filteredMessages.Count)
            ? _filteredMessages[MsgBox.SelectedIndex]
            : null;

    private byte[] EncodeCurrent(DbcMessage msg)
    {
        var vals = new Dictionary<string, double>();
        foreach (var r in _signals)
            if (r.TryParseValue(out var v)) vals[r.Name] = v;
        return msg.EncodeMessage(vals);
    }

    // ----- send actions --------------------------------------------------------

    private void OnSendOnce(object sender, RoutedEventArgs e)
    {
        if (!Validate(out var bus, out var msg, out var data)) return;
        CanFramePostProcessor.Apply(msg, data!, new FrameProtectionState());
        var ok = _vm.Send(bus!, msg!.Id, data!, extended: msg!.Id > 0x7FF, fd: bus!.IsFd, brs: bus!.IsFd);
        SetStatus(ok ? $"Sent '{msg!.Name}' on {bus!.Name}." : $"Send failed.", ok);
    }

    private void OnStartCyclic(object sender, RoutedEventArgs e)
    {
        if (!Validate(out var bus, out var msg, out var data)) return;
        if (!int.TryParse(PeriodBox.Text.Trim(), out var period) || period < 10)
        { SetStatus("Period must be an integer ≥ 10 ms.", false); return; }

        _vm.Cyclic.Start(bus!, msg!.Id, data!,
                         extended: msg!.Id > 0x7FF,
                         fd: bus!.IsFd, brs: bus!.IsFd,
                         periodMs: period, messageName: msg!.Name,
                         dbcMessage: msg);
        SetStatus($"Cyclic started: '{msg!.Name}' every {period} ms on {bus!.Name}.", true);
    }

    private bool Validate(out CanBus? bus, out DbcMessage? msg, out byte[]? data)
    {
        bus = null; msg = null; data = null;
        if (BusBox.SelectedItem is not CanBus b) { SetStatus("Select a bus.", false); return false; }
        var m = SelectedMessage();
        if (m is null) { SetStatus("Select a message.", false); return false; }

        try { data = EncodeCurrent(m); }
        catch (Exception ex) { SetStatus("Encode failed: " + ex.Message, false); return false; }

        bus = b; msg = m;
        return true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void SetStatus(string text, bool ok)
    {
        StatusBlock.Foreground = ok ? Brushes.DarkGreen : Brushes.DarkRed;
        StatusBlock.Text = text;
    }
}
