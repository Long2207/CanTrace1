// ============================================================================
// MainViewModel.cs (v5)
// Changes from v4:
//  - "Add Bus" replaced by "Settings…" (bulk add via SettingsDialog).
//  - Record button writes into Logs/<timestamp>/capture.blf automatically
//    (no SaveFileDialog prompt — fast, predictable layout).
//  - Added "Open Logs Folder" command.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CanTracer.Models;
using CanTracer.Services;
using Microsoft.Win32;

namespace CanTracer.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly BusManager   _busMgr = new();
    private readonly TestCaseSender _tcSender = new();
    private readonly BlfRecorder  _recorder = new();
    public  readonly CyclicSender Cyclic;

    private readonly DispatcherTimer _flushTimer;
    private readonly object _bufLock = new();
    private readonly List<CanMessage> _buf = new();
    private readonly Dictionary<(string, uint), AggregatedMessage> _agg = new();

    public ObservableCollection<AggregatedMessage> Messages { get; } = new();
    public ObservableCollection<CanBus>            Buses    => _busMgr.Buses;

    // Testcase browser: list of .mtc files found under the TestCases/ folder.
    public ObservableCollection<TestCaseFile> TestCases { get; } = new();

    private MtcTestCase? _loadedTestCase;
    private string _tcStatus = "";
    public string TestCaseStatus { get => _tcStatus; set { _tcStatus = value; Notify(); } }

    // Send Messenger panel: messages of the loaded testcase, with decoded signals.
    public ObservableCollection<SendMessageVm> SendMessages { get; } = new();
    private bool _showSendPanel;
    public bool ShowSendPanel { get => _showSendPanel; set { _showSendPanel = value; Notify(); } }

    public bool IsFiringTestCase => _tcSender.IsRunning;
    public string FireButtonText => _tcSender.IsRunning ? "■ Stop testcase" : "▶ Fire testcase";
    public string LoadedTestCaseName => _loadedTestCase?.Name ?? "(none loaded)";
    public ICollectionView MessagesView { get; }

    // ---- search + status ------------------------------------------------------

    private string _search = "";
    public string Search
    {
        get => _search;
        set { _search = value ?? ""; Notify(); MessagesView.Refresh(); }
    }

    private string _status = "Ready";
    public string Status { get => _status; set { _status = value; Notify(); } }

    public bool HasNoBuses    => Buses.Count == 0;
    public int  UniqueMessages => Messages.Count;

    // ---- recording state ------------------------------------------------------

    public bool   IsRecording      => _recorder.IsRecording;
    public int    RecordFrameCount => _recorder.FrameCount;
    public string RecordPath       => _recorder.Path;
    public string RecordButtonText => _recorder.IsRecording ? "■ Stop record" : "⏺ Record BLF";
    private readonly DispatcherTimer _recordRefreshTimer;

    // ---- commands -------------------------------------------------------------

    public RelayCommand OpenSettingsCommand  { get; }
    public RelayCommand RemoveBusCommand     { get; }
    public RelayCommand ConnectBusCommand    { get; }
    public RelayCommand DisconnectBusCommand { get; }
    public RelayCommand StartBusCommand      { get; }
    public RelayCommand StopBusCommand       { get; }
    public RelayCommand LoadDbcCommand       { get; }
    public RelayCommand SendByDbcCommand     { get; }
    public RelayCommand ClearCommand         { get; }
    public RelayCommand ResetCountsCommand   { get; }
    public RelayCommand ExportCsvCommand     { get; }
    public RelayCommand ToggleRecordCommand  { get; }
    public RelayCommand OpenLogsFolderCommand{ get; }
    public RelayCommand StopCyclicCommand    { get; }
    public RelayCommand RefreshTestCasesCommand { get; }
    public RelayCommand OpenTestCaseCommand     { get; }
    public RelayCommand FireTestCaseCommand     { get; }
    public RelayCommand NewTestCaseCommand      { get; }
    public RelayCommand EditTestCaseCommand     { get; }
    public RelayCommand OpenTestCasesFolderCommand { get; }
    public RelayCommand RemoveSendMessageCommand   { get; }
    public RelayCommand AddSendMessageCommand      { get; }

    public event Action? RequestSettingsDialog;
    public event Action? RequestSendByDbcDialog;
    /// <summary>Raised to open the testcase editor. Arg = file to edit, or null for new.</summary>
    public event Action<TestCaseFile?>? RequestTestCaseEditor;
    /// <summary>Raised to open the "add message" picker for the Send Messenger.</summary>
    public event Action? RequestAddMessageDialog;

    public MainViewModel()
    {
        Cyclic = new CyclicSender(_busMgr);

        MessagesView = CollectionViewSource.GetDefaultView(Messages);
        MessagesView.Filter = FilterPredicate;
        MessagesView.SortDescriptions.Add(new SortDescription(nameof(AggregatedMessage.BusName), ListSortDirection.Ascending));
        MessagesView.SortDescriptions.Add(new SortDescription(nameof(AggregatedMessage.Id),      ListSortDirection.Ascending));

        OpenSettingsCommand  = new RelayCommand(_ => RequestSettingsDialog?.Invoke());
        RemoveBusCommand     = new RelayCommand(p => { if (p is CanBus b) { _busMgr.Remove(b); SaveConfig(); } });
        ConnectBusCommand    = new RelayCommand(p => { if (p is CanBus b) ConnectBus(b); });
        DisconnectBusCommand = new RelayCommand(p => { if (p is CanBus b) _busMgr.Disconnect(b); });
        StartBusCommand      = new RelayCommand(p => { if (p is CanBus b) _busMgr.StartTrace(b); });
        StopBusCommand       = new RelayCommand(p => { if (p is CanBus b) _busMgr.StopTrace(b); });
        LoadDbcCommand       = new RelayCommand(p => { if (p is CanBus b) LoadDbc(b); });
        SendByDbcCommand     = new RelayCommand(_ => RequestSendByDbcDialog?.Invoke(),
                                                _ => Buses.Any(b => b.IsConnected));
        ClearCommand         = new RelayCommand(_ => Clear());
        ResetCountsCommand   = new RelayCommand(_ => ResetCounts());
        ExportCsvCommand     = new RelayCommand(_ => ExportCsv(), _ => Messages.Count > 0);
        ToggleRecordCommand  = new RelayCommand(_ => ToggleRecord());
        OpenLogsFolderCommand = new RelayCommand(_ => LogFolderManager.OpenRoot());
        StopCyclicCommand    = new RelayCommand(p => { if (p is Guid id) Cyclic.Stop(id); });
        RefreshTestCasesCommand = new RelayCommand(_ => RefreshTestCases());
        OpenTestCaseCommand     = new RelayCommand(p => { if (p is TestCaseFile f) LoadTestCase(f); });
        FireTestCaseCommand     = new RelayCommand(_ => ToggleFireTestCase(),
                                                   _ => _loadedTestCase != null);
        NewTestCaseCommand      = new RelayCommand(_ => RequestTestCaseEditor?.Invoke(null));
        EditTestCaseCommand     = new RelayCommand(p =>
        {
            var f = p as TestCaseFile ?? TestCases.FirstOrDefault(x => x.Path == _loadedTestCase?.FilePath);
            if (f != null) RequestTestCaseEditor?.Invoke(f);
        });
        OpenTestCasesFolderCommand = new RelayCommand(_ => TestCaseFolder.OpenRoot());
        RemoveSendMessageCommand   = new RelayCommand(p => { if (p is SendMessageVm vm) RemoveSendMessage(vm); });
        AddSendMessageCommand      = new RelayCommand(_ => RequestAddMessageDialog?.Invoke(),
                                                      _ => _loadedTestCase != null);

        _tcSender.Report += msg => Application.Current?.Dispatcher.Invoke(() => TestCaseStatus = msg);
        RefreshTestCases();
        _busMgr.FrameReceived += OnFrameReceived;
        _busMgr.ErrorOccurred += msg => Application.Current?.Dispatcher.Invoke(() => Status = "Error: " + msg);
        _busMgr.Buses.CollectionChanged += (_, _) => Notify(nameof(HasNoBuses));

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();

        _recordRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _recordRefreshTimer.Tick += (_, _) =>
        {
            if (_recorder.IsRecording) Notify(nameof(RecordFrameCount));
        };
        _recordRefreshTimer.Start();

        // Restore the bus configuration saved from the last session.
        LoadSavedConfig();
    }

    // ---- testcase browser -----------------------------------------------------

    /// <summary>Rescan the TestCases/ folder and rebuild the list.</summary>
    public void RefreshTestCases()
    {
        TestCases.Clear();
        foreach (var path in MtcStore.EnumerateFiles(TestCaseFolder.Root))
            TestCases.Add(new TestCaseFile(path));
        TestCaseStatus = $"{TestCases.Count} testcase(s) in {TestCaseFolder.Root}";
    }

    /// <summary>Load (parse) a .mtc file so it can be fired.</summary>
    private void LoadTestCase(TestCaseFile file)
    {
        try
        {
            _loadedTestCase = MtcStore.Load(file.Path);
            BuildSendMessenger();
            Notify(nameof(LoadedTestCaseName));
            FireTestCaseCommand.RaiseCanExecuteChanged();
            var tags = string.Join(", ", _loadedTestCase.BusFiles.Select(b => b.BusTag));
            TestCaseStatus = $"Loaded '{_loadedTestCase.Name}': {_loadedTestCase.TotalMessages} msg across [{tags}]";
        }
        catch (Exception ex)
        {
            TestCaseStatus = "Failed to load: " + ex.Message;
        }
    }

    /// <summary>Build the Send Messenger panel rows from the loaded testcase,
    /// pairing each signal with its DBC definition (for value-table decode).</summary>
    private void BuildSendMessenger()
    {
        SendMessages.Clear();
        if (_loadedTestCase == null) { ShowSendPanel = false; return; }

        foreach (var busFile in _loadedTestCase.BusFiles)
        {
            var bus = TestCaseSender.ResolveBus(busFile.BusTag, Buses.ToList());
            foreach (var msg in busFile.Data)
            {
                DbcMessage? dbcMsg = null;
                if (bus != null)
                {
                    if (TestCaseSender.TryParseIdStatic(msg.ID, out var id)
                        && bus.Dbc.TryGetValue(id, out var byId))
                        dbcMsg = byId;
                    else
                        dbcMsg = bus.Dbc.Values.FirstOrDefault(
                            m => string.Equals(m.Name, msg.Name, StringComparison.OrdinalIgnoreCase));
                }
                SendMessages.Add(new SendMessageVm(msg, dbcMsg, busFile.BusTag));
            }
        }
        ShowSendPanel = SendMessages.Count > 0;
    }

    /// <summary>Remove a single message row from the Send Messenger (and the testcase).</summary>
    public void RemoveSendMessage(SendMessageVm vm)
    {
        SendMessages.Remove(vm);
        if (_loadedTestCase != null)
            foreach (var bf in _loadedTestCase.BusFiles)
                bf.Data.Remove(vm.Underlying);
        ShowSendPanel = SendMessages.Count > 0;
    }

    /// <summary>
    /// Add a message to the loaded testcase's first bus file. If the DBC of the
    /// resolved bus contains a message matching idOrName (by 0x-id or by name),
    /// its signals are pre-filled (value 0); otherwise an empty message is added.
    /// Returns an error string, or null on success.
    /// </summary>
    public string? AddSendMessage(string idOrName)
    {
        if (_loadedTestCase == null) return "No testcase loaded.";
        if (_loadedTestCase.BusFiles.Count == 0) return "Testcase has no bus.";
        idOrName = idOrName.Trim();
        if (string.IsNullOrEmpty(idOrName)) return "Enter a message ID or name.";

        var busFile = _loadedTestCase.BusFiles[0];
        var bus = TestCaseSender.ResolveBus(busFile.BusTag, Buses.ToList());

        // Try to find the message in the bus DBC.
        DbcMessage? dbcMsg = null;
        if (bus != null)
        {
            if (TestCaseSender.TryParseIdStatic(idOrName, out var id)
                && bus.Dbc.TryGetValue(id, out var byId))
                dbcMsg = byId;
            else
                dbcMsg = bus.Dbc.Values.FirstOrDefault(
                    m => string.Equals(m.Name, idOrName, StringComparison.OrdinalIgnoreCase));
        }

        var msg = new MtcMessage
        {
            ID   = dbcMsg != null ? $"0x{dbcMsg.Id:X3}" : idOrName,
            Name = dbcMsg?.Name ?? idOrName,
            CycleTime = 100,
        };
        if (dbcMsg != null)
            foreach (var s in dbcMsg.Signals)
                msg.SignalItem.Add(new MtcSignal { CanSignalName = s.Name, Value = "0", Type = 1 });

        busFile.Data.Add(msg);
        SendMessages.Add(new SendMessageVm(msg, dbcMsg, busFile.BusTag));
        ShowSendPanel = true;
        return null;
    }

    /// <summary>Names of all DBC messages available across configured buses (for the add picker).</summary>
    public IEnumerable<string> AvailableDbcMessageNames()
    {
        return Buses.SelectMany(b => b.Dbc.Values)
                    .Select(m => $"{m.Name}  (0x{m.Id:X3})")
                    .Distinct()
                    .OrderBy(s => s);
    }

    /// <summary>
    /// Look up a DBC message for a given bus tag (resolving alias) by ID or name.
    /// Returns null if no configured bus / DBC match. Used by the testcase editor
    /// to auto-fill signals when a message is added.
    /// </summary>
    public DbcMessage? LookupDbcMessage(string busTag, string idOrName)
    {
        var bus = TestCaseSender.ResolveBus(busTag, Buses.ToList());
        if (bus == null) return null;
        idOrName = idOrName.Trim();

        if (TestCaseSender.TryParseIdStatic(idOrName, out var id)
            && bus.Dbc.TryGetValue(id, out var byId))
            return byId;

        return bus.Dbc.Values.FirstOrDefault(
            m => string.Equals(m.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>All DBC message names for a specific bus tag (for the editor's picker).</summary>
    public IEnumerable<string> DbcMessageNamesForTag(string busTag)
    {
        var bus = TestCaseSender.ResolveBus(busTag, Buses.ToList());
        if (bus == null) return Enumerable.Empty<string>();
        return bus.Dbc.Values
                  .Select(m => $"{m.Name}  (0x{m.Id:X3})")
                  .OrderBy(s => s);
    }

    /// <summary>Start or stop firing the loaded testcase.</summary>
    private void ToggleFireTestCase()
    {
        if (_tcSender.IsRunning)
        {
            _tcSender.Stop();
            TestCaseStatus = "Testcase stopped";
        }
        else if (_loadedTestCase != null)
        {
            // Ensure target buses are connected + tracing before firing.
            var n = _tcSender.Prepare(_loadedTestCase, Buses.ToList(), _busMgr);
            if (n == 0)
            {
                TestCaseStatus = "Nothing to fire — check bus names match the testcase tags";
            }
            else
            {
                _tcSender.Start();
                TestCaseStatus = $"Firing {n} message(s)…";
            }
        }
        Notify(nameof(IsFiringTestCase));
        Notify(nameof(FireButtonText));
    }

    // ---- default buses --------------------------------------------------------

    /// <summary>The 8 standard CAN lines, created on very first launch.</summary>
    public static readonly string[] DefaultBusNames =
        { "ICAN", "BCAN", "CCAN", "PCAN", "LCAN", "SCAN", "BACAN", "REEVCAN" };

    private static readonly string[] DefaultBusColors =
        { "#FF1E88E5", "#FF43A047", "#FFE53935", "#FFFB8C00",
          "#FF8E24AA", "#FF00897B", "#FF6D4C41", "#FF3949AB" };

    /// <summary>Create the 8 default buses (no channel/DBC yet — user sets via Settings).</summary>
    private void SeedDefaultBuses()
    {
        for (int i = 0; i < DefaultBusNames.Length; i++)
        {
            var bus = new CanBus
            {
                Name      = DefaultBusNames[i],
                ColorHex  = DefaultBusColors[i],
            };
            try
            {
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                            .ConvertFromString(DefaultBusColors[i]);
                bus.ColorBrush = new System.Windows.Media.SolidColorBrush(c);
            }
            catch { }
            _busMgr.Add(bus);
        }
        SaveConfig();
        Status = $"Created {DefaultBusNames.Length} default buses — configure channels & DBC in Settings";
        Notify(nameof(HasNoBuses));
    }

    // ---- config persistence ---------------------------------------------------

    /// <summary>Load config.json and recreate the saved buses (no auto-connect).</summary>
    private void LoadSavedConfig()
    {
        var cfg = ConfigStore.Load();
        if (cfg.Buses.Count == 0)
        {
            SeedDefaultBuses();
            return;
        }

        foreach (var bc in cfg.Buses)
        {
            var bus = new CanBus
            {
                Name         = bc.Name,
                ChannelId    = bc.ChannelId,
                ChannelLabel = bc.ChannelLabel,
                IsFd         = bc.IsFd,
                Baud         = bc.Baud,
                FdNominal    = bc.FdNominal,
                FdData       = bc.FdData,
                ColorHex     = string.IsNullOrEmpty(bc.ColorHex) ? "#FF808080" : bc.ColorHex,
            };
            try
            {
                var col = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                              .ConvertFromString(bus.ColorHex);
                bus.ColorBrush = new System.Windows.Media.SolidColorBrush(col);
            }
            catch { }
            var dbcPath = bc.DbcPath;
            _busMgr.Add(bus);
            if (!string.IsNullOrEmpty(dbcPath) && File.Exists(dbcPath))
            {
                try { _busMgr.LoadDbc(bus, dbcPath); }
                catch { /* DBC moved/deleted — bus still usable without decode */ }
            }
        }
        Status = $"Restored {cfg.Buses.Count} bus(es) from saved config";
        Notify(nameof(HasNoBuses));
    }

    /// <summary>Write the current buses to config.json.</summary>
    private void SaveConfig()
    {
        var cfg = new AppConfig
        {
            Buses = Buses.Select(b => new BusConfig
            {
                Name         = b.Name,
                ChannelId    = b.ChannelId,
                ChannelLabel = b.ChannelLabel,
                IsFd         = b.IsFd,
                Baud         = b.Baud,
                FdNominal    = b.FdNominal,
                FdData       = b.FdData,
                DbcPath      = b.DbcPath,
                ColorHex     = b.ColorHex,
            }).ToList()
        };
        ConfigStore.Save(cfg);
    }

    // ---- bus lifecycle --------------------------------------------------------

    /// <summary>Called by MainWindow after SettingsDialog returns the list of buses.</summary>
    public void ApplySettings(List<CanBus> newBuses)
    {
        // Simple strategy: stop & remove all current buses, then add the new ones.
        var existing = _busMgr.Buses.ToList();
        foreach (var b in existing) _busMgr.Remove(b);

        Messages.Clear();
        _agg.Clear();
        Notify(nameof(UniqueMessages));

        foreach (var bus in newBuses)
        {
            var dbcPath = bus.DbcPath;
            bus.DbcPath = "";
            _busMgr.Add(bus);
            if (!string.IsNullOrEmpty(dbcPath) && File.Exists(dbcPath))
            {
                try { _busMgr.LoadDbc(bus, dbcPath); }
                catch (Exception ex) { Status = $"DBC load failed for {bus.Name}: {ex.Message}"; }
            }
        }
        SaveConfig();   // persist so the config survives restart
        Status = $"Settings applied & saved: {newBuses.Count} bus(es)";
        Notify(nameof(HasNoBuses));
    }

    private void ConnectBus(CanBus b)
    {
        if (_busMgr.Connect(b))
        {
            // Per requirement: connecting a bus auto-starts its trace.
            _busMgr.StartTrace(b);
            Status = $"{b.Name}: connected & tracing";
        }
        else
        {
            Status = $"{b.Name}: connect failed";
        }
    }

    /// <summary>
    /// Called from the Settings dialog's per-row Connect button. Registers the
    /// bus (replacing any existing bus of the same name), loads its DBC, then
    /// connects and auto-starts tracing. Does not close the dialog.
    /// </summary>
    public void ConnectSingleBusFromSettings(CanBus bus)
    {
        // Replace an existing bus with the same name (re-configuring it).
        var existing = _busMgr.Buses.FirstOrDefault(
            b => string.Equals(b.Name, bus.Name, System.StringComparison.OrdinalIgnoreCase));
        if (existing != null) _busMgr.Remove(existing);

        var dbcPath = bus.DbcPath;
        bus.DbcPath = "";
        _busMgr.Add(bus);
        if (!string.IsNullOrEmpty(dbcPath) && File.Exists(dbcPath))
        {
            try { _busMgr.LoadDbc(bus, dbcPath); }
            catch (Exception ex) { Status = $"DBC load failed for {bus.Name}: {ex.Message}"; }
        }

        ConnectBus(bus);   // connects + auto-starts trace
        SaveConfig();
    }

    private void LoadDbc(CanBus b)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "DBC files (*.dbc)|*.dbc|All files (*.*)|*.*",
            Title  = $"Open DBC for '{b.Name}'"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _busMgr.LoadDbc(b, dlg.FileName);
            foreach (var m in Messages.Where(m => m.BusName == b.Name && string.IsNullOrEmpty(m.Name)))
                if (b.Dbc.TryGetValue(m.Id, out var dbc)) m.Name = dbc.Name;
            Status = $"{b.Name}: DBC loaded ({b.Dbc.Count})";
        }
        catch (Exception ex)
        {
            MessageBox.Show("DBC load failed: " + ex.Message, "DBC", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public IReadOnlyDictionary<uint, DbcMessage>? GetDbcFor(CanBus bus)
        => bus.Dbc.Count > 0 ? bus.Dbc : null;

    public bool Send(CanBus bus, uint id, byte[] data, bool extended, bool fd, bool brs)
        => _busMgr.Send(bus, id, data, extended, fd, brs);

    // ---- frame ingest ---------------------------------------------------------

    private void OnFrameReceived(CanMessage f)
    {
        _recorder.Write(f);
        lock (_bufLock) _buf.Add(f);
    }

    private void Flush()
    {
        List<CanMessage>? batch = null;
        lock (_bufLock)
        {
            if (_buf.Count > 0) { batch = new List<CanMessage>(_buf); _buf.Clear(); }
        }
        if (batch is null) return;

        bool grew = false;
        foreach (var f in batch)
        {
            var key = (f.BusName, f.Id);
            if (!_agg.TryGetValue(key, out var row))
            {
                row = new AggregatedMessage
                {
                    BusName    = f.BusName,
                    BusColor   = f.BusColor,
                    Id         = f.Id,
                    IsExtended = f.IsExtended,
                    Name       = f.Name,
                    EventType  = f.EventType,
                };
                _agg[key] = row;
                Messages.Add(row);
                grew = true;
            }
            row.Update(f);
            var bus = Buses.FirstOrDefault(b => b.Name == f.BusName);
            if (bus != null) bus.FrameCount++;
        }
        if (grew) Notify(nameof(UniqueMessages));
    }

    private bool FilterPredicate(object obj)
    {
        if (obj is not AggregatedMessage m) return false;
        if (string.IsNullOrWhiteSpace(_search)) return true;
        var s = _search.Trim();
        return m.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
            || m.IdHex.Contains(s, StringComparison.OrdinalIgnoreCase)
            || m.BusName.Equals(s, StringComparison.OrdinalIgnoreCase);
    }

    // ---- clear / reset / export ----------------------------------------------

    private void Clear()
    {
        Messages.Clear();
        _agg.Clear();
        foreach (var b in Buses) b.FrameCount = 0;
        Notify(nameof(UniqueMessages));
    }

    private void ResetCounts()
    {
        foreach (var m in Messages) { m.Count = 0; m.CycleMs = 0; }
    }

    private void ExportCsv()
    {
        // Save CSV into the Logs root so it's easy to find next to BLF sessions.
        var path = Path.Combine(LogFolderManager.RootFolder,
                                $"messages_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        //CsvExporter.Export(path, Messages);
        Status = "Exported: " + Path.GetFileName(path);
    }

    // ---- BLF record into Logs/<timestamp>/ -----------------------------------

    private void ToggleRecord()
    {
        if (_recorder.IsRecording)
        {
            var path = _recorder.Path;
            var n    = _recorder.FrameCount;
            _recorder.Stop();
            Status = $"Recording stopped — {n} frames saved to {Path.GetFileName(Path.GetDirectoryName(path)!)}/";
        }
        else
        {
            // Auto-create Logs/yyyy-MM-dd_HH-mm-ss/capture.blf — no prompt.
            var sessionFolder = LogFolderManager.CreateNewSession();
            var blfPath = Path.Combine(sessionFolder, "capture.blf");
            _recorder.Start(blfPath);
            Status = "Recording → Logs/" + Path.GetFileName(sessionFolder) + "/capture.blf";
        }
        Notify(nameof(IsRecording));
        Notify(nameof(RecordButtonText));
        Notify(nameof(RecordPath));
        Notify(nameof(RecordFrameCount));
    }

    // ---- INPC -----------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
