// ============================================================================
// SettingsDialog.xaml.cs (v7)
// Now supports editing an existing configuration:
//   - Opens pre-populated with the buses currently configured.
//   - Each row has an ✕ delete button.
//   - "+ Add bus" appends a blank row.
//   - "Apply & Save" returns the full list (MainViewModel persists + applies).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CanTracer.Models;
using CanTracer.Services;
using Microsoft.Win32;

namespace CanTracer.Views;

public partial class SettingsDialog : Window, INotifyPropertyChanged
{
    public sealed class BusRowVm : INotifyPropertyChanged
    {
        private string _busName = "";
        public string BusName { get => _busName; set { _busName = value; Notify(); } }

        private string _dbcPath = "";
        public string DbcPath { get => _dbcPath; set { _dbcPath = value; Notify(); } }

        private ChannelInfo? _selectedChannel;
        public ChannelInfo? SelectedChannel { get => _selectedChannel; set { _selectedChannel = value; Notify(); } }

        private bool _isFd;
        public bool IsFd { get => _isFd; set { _isFd = value; Notify(); } }

        // Carried over from saved config so we can re-select the channel even
        // if the device isn't currently plugged in.
        public string SavedChannelId    { get; set; } = "";
        public string SavedChannelLabel { get; set; } = "";
        public string FdNominal { get; set; } = "500";
        public string FdData    { get; set; } = "2000";
        public string Baud      { get; set; } = "500 kbit/s";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private readonly ObservableCollection<BusRowVm> _rows = new();
    public ObservableCollection<ChannelInfo> AvailableChannels { get; } = new();

    public List<CanBus>? Result { get; private set; }

    /// <summary>Raised when the user clicks Connect on a single row.
    /// MainWindow handles it by registering + connecting that bus immediately
    /// (without closing the dialog). Connecting auto-starts the trace.</summary>
    public event Action<CanBus>? ConnectRequested;

    /// <param name="existing">Buses currently configured, used to pre-populate rows.</param>
    public SettingsDialog(IEnumerable<CanBus>? existing = null)
    {
        InitializeComponent();
        DataContext = this;
        BusRows.ItemsSource = _rows;

        Rescan();
        LoadExisting(existing);
    }

    // ----- populate from existing buses ---------------------------------------

    private void LoadExisting(IEnumerable<CanBus>? existing)
    {
        var list = existing?.ToList() ?? new List<CanBus>();
        if (list.Count == 0)
        {
            // No buses yet → start with two blank rows for convenience.
            AddBlankRow();
            AddBlankRow();
            return;
        }

        foreach (var b in list)
        {
            var row = new BusRowVm
            {
                BusName           = b.Name,
                DbcPath           = b.DbcPath,
                IsFd              = b.IsFd,
                FdNominal         = b.FdNominal,
                FdData            = b.FdData,
                Baud              = b.Baud,
                SavedChannelId    = b.ChannelId,
                SavedChannelLabel = b.ChannelLabel,
            };
            // Try to match the saved channel to a currently-detected one.
            row.SelectedChannel = AvailableChannels.FirstOrDefault(c => c.Id == b.ChannelId);
            // If the device isn't plugged in right now, synthesize a placeholder
            // entry so the dropdown still shows what was saved.
            if (row.SelectedChannel == null && !string.IsNullOrEmpty(b.ChannelId))
            {
                var placeholder = new ChannelInfo
                {
                    Supplier   = b.ChannelId.StartsWith("VECTOR:") ? CanSupplier.Vector : CanSupplier.Peak,
                    Id         = b.ChannelId,
                    Label      = b.ChannelLabel + "  (not detected)",
                    SupportsFd = true,
                };
                AvailableChannels.Add(placeholder);
                row.SelectedChannel = placeholder;
            }
            _rows.Add(row);
        }
    }

    // ----- channel discovery --------------------------------------------------

    private void OnRescan(object sender, RoutedEventArgs e) => Rescan();

    private void Rescan()
    {
        // Keep any placeholder (not-detected) entries that rows still reference.
        var referenced = _rows.Select(r => r.SelectedChannel).Where(c => c != null).ToList();

        AvailableChannels.Clear();
        try
        {
            foreach (var ch in ChannelDiscovery.Discover())
                AvailableChannels.Add(ch);

            // Re-add placeholders for saved channels that aren't physically present.
            foreach (var c in referenced)
                if (c != null && AvailableChannels.All(x => x.Id != c.Id))
                    AvailableChannels.Add(c);

            var peak   = AvailableChannels.Count(c => c.Supplier == CanSupplier.Peak);
            var vector = AvailableChannels.Count(c => c.Supplier == CanSupplier.Vector);
            DetectInfo.Text = $"Detected: {peak} PEAK + {vector} Vector channel(s)";
        }
        catch (Exception ex)
        {
            DetectInfo.Text = "Detect failed: " + ex.Message;
        }
    }

    // ----- add / delete rows --------------------------------------------------

    private void OnAddRow(object sender, RoutedEventArgs e) => AddBlankRow();

    private void AddBlankRow()
    {
        var row = new BusRowVm();
        // Pre-select the first not-yet-used channel if available.
        var used = _rows.Select(r => r.SelectedChannel?.Id).Where(id => id != null).ToHashSet();
        row.SelectedChannel = AvailableChannels.FirstOrDefault(c => !used.Contains(c.Id))
                              ?? AvailableChannels.FirstOrDefault();
        _rows.Add(row);
    }

    private void OnDeleteRow(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is BusRowVm row)
            _rows.Remove(row);
    }

    // ----- browse DBC ---------------------------------------------------------

    private void OnBrowseDbc(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not BusRowVm row) return;
        var dlg = new OpenFileDialog
        {
            Filter = "DBC files (*.dbc)|*.dbc|All files (*.*)|*.*",
            Title  = "Open DBC file"
        };
        if (dlg.ShowDialog() == true)
        {
            row.DbcPath = dlg.FileName;
            if (string.IsNullOrWhiteSpace(row.BusName))
            {
                var stem = Path.GetFileNameWithoutExtension(dlg.FileName);
                foreach (var tok in stem.Split('_', '-', '.'))
                    if (tok.Length >= 3 && char.IsLetter(tok[0])
                        && tok.IndexOf("CAN", StringComparison.OrdinalIgnoreCase) >= 0)
                    { row.BusName = tok.ToUpperInvariant(); break; }
            }
        }
    }

    // ----- apply --------------------------------------------------------------

    private void OnConnectRow(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not BusRowVm row) return;

        if (string.IsNullOrWhiteSpace(row.BusName) || row.SelectedChannel == null)
        {
            StatusBlock.Text = "This bus needs a name and a channel before connecting.";
            return;
        }

        var bus = new CanBus
        {
            Name         = row.BusName.Trim(),
            ChannelId    = row.SelectedChannel.Id,
            ChannelLabel = row.SelectedChannel.Label.Replace("  (not detected)", ""),
            IsFd         = row.IsFd,
            Baud         = row.Baud,
            FdNominal    = row.FdNominal,
            FdData       = row.FdData,
            DbcPath      = row.DbcPath,
        };
        ConnectRequested?.Invoke(bus);
        StatusBlock.Text = $"Connecting '{bus.Name}'…";
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        StatusBlock.Text = "";

        var active = _rows.Where(r => !string.IsNullOrWhiteSpace(r.BusName)
                                   && !string.IsNullOrWhiteSpace(r.DbcPath)
                                   && r.SelectedChannel != null).ToList();
        if (active.Count == 0)
        {
            StatusBlock.Text = "Add at least one bus with name, channel, and DBC.";
            return;
        }

        var dupChan = active.GroupBy(r => r.SelectedChannel!.Id).FirstOrDefault(g => g.Count() > 1);
        if (dupChan != null)
        {
            StatusBlock.Text = $"Channel '{dupChan.First().SelectedChannel!.Label}' is used by multiple buses.";
            return;
        }
        var dupName = active.GroupBy(r => r.BusName.Trim()).FirstOrDefault(g => g.Count() > 1);
        if (dupName != null)
        {
            StatusBlock.Text = $"Bus name '{dupName.Key}' is used more than once.";
            return;
        }

        Result = active.Select(r => new CanBus
        {
            Name         = r.BusName.Trim(),
            ChannelId    = r.SelectedChannel!.Id,
            ChannelLabel = r.SelectedChannel!.Label.Replace("  (not detected)", ""),
            IsFd         = r.IsFd,
            Baud         = r.Baud,
            FdNominal    = r.FdNominal,
            FdData       = r.FdData,
            DbcPath      = r.DbcPath,
        }).ToList();

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    public event PropertyChangedEventHandler? PropertyChanged;
}
