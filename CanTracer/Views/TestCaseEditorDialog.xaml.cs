// ============================================================================
// TestCaseEditorDialog.xaml.cs
// Create or edit a .mtc testcase.
//
// Layout: left = messages of the currently-selected bus tag; right = signals of
// the selected message. Top has the testcase name + a bus-tag selector (a .mtc
// can target several buses, one JSON file each).
//
// Save writes a .mtc into the TestCases/ folder via MtcStore.
// ============================================================================
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CanTracer.Models;
using CanTracer.Services;
using CanTracer.ViewModels;

namespace CanTracer.Views;

public partial class TestCaseEditorDialog : Window
{
    private readonly MainViewModel _vm;
    private MtcTestCase _tc;

    // Currently-displayed bus file (one tab/tag).
    private MtcBusFile? _currentBusFile;
    private readonly ObservableCollection<MtcMessage> _messages = new();
    private readonly ObservableCollection<MtcSignal>  _signals  = new();

    public TestCaseEditorDialog(MainViewModel vm, TestCaseFile? file)
    {
        InitializeComponent();
        _vm = vm;

        if (file != null)
        {
            // Edit existing.
            try { _tc = MtcStore.Load(file.Path); }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to open testcase: " + ex.Message);
                _tc = NewEmptyTestCase();
            }
        }
        else
        {
            _tc = NewEmptyTestCase();
        }

        TcNameBox.Text = _tc.Name;
        MessagesGrid.ItemsSource = _messages;
        SignalsGrid.ItemsSource  = _signals;

        RebuildBusTagCombo();
    }

    private MtcTestCase NewEmptyTestCase()
    {
        // Seed with one bus file using the first default bus name.
        var tc = new MtcTestCase { Name = "New Testcase" };
        var first = MainViewModel.DefaultBusNames.FirstOrDefault() ?? "ICAN";
        tc.BusFiles.Add(new MtcBusFile
        {
            BusTag = first,
            EntryName = $"[{first}]{first}",
        });
        return tc;
    }

    // ----- bus tag selector ---------------------------------------------------

    private void RebuildBusTagCombo()
    {
        BusTagCombo.SelectionChanged -= OnBusTagChanged;
        BusTagCombo.Items.Clear();
        foreach (var bf in _tc.BusFiles)
            BusTagCombo.Items.Add(bf.BusTag);
        BusTagCombo.SelectionChanged += OnBusTagChanged;

        if (BusTagCombo.Items.Count > 0)
            BusTagCombo.SelectedIndex = 0;
    }

    private void OnBusTagChanged(object sender, SelectionChangedEventArgs e)
    {
        var tag = BusTagCombo.SelectedItem as string;
        _currentBusFile = _tc.BusFiles.FirstOrDefault(b => b.BusTag == tag);
        ReloadMessages();
    }

    private void ReloadMessages()
    {
        _messages.Clear();
        if (_currentBusFile != null)
            foreach (var m in _currentBusFile.Data) _messages.Add(m);
        _signals.Clear();
        SignalsHeader.Text = "Signals";
    }

    private void OnAddBusTag(object sender, RoutedEventArgs e)
    {
        // Offer the default bus names not already present.
        var used = _tc.BusFiles.Select(b => b.BusTag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = MainViewModel.DefaultBusNames.Where(n => !used.Contains(n)).ToList();
        if (available.Count == 0)
        {
            StatusBlock.Text = "All default bus tags are already in this testcase.";
            return;
        }
        var tag = available[0];
        _tc.BusFiles.Add(new MtcBusFile { BusTag = tag, EntryName = $"[{tag}]{tag}" });
        RebuildBusTagCombo();
        BusTagCombo.SelectedItem = tag;
        StatusBlock.Text = $"Added bus tag '{tag}'.";
    }

    // ----- messages -----------------------------------------------------------

    private void OnMessageSelected(object sender, SelectionChangedEventArgs e)
    {
        var msg = MessagesGrid.SelectedItem as MtcMessage;
        _signals.Clear();
        if (msg != null)
        {
            foreach (var s in msg.SignalItem) _signals.Add(s);
            SignalsHeader.Text = $"Signals of {msg.Name} ({msg.ID})";
        }
        else SignalsHeader.Text = "Signals";
    }

    private void OnAddMessage(object sender, RoutedEventArgs e)
    {
        if (_currentBusFile == null) { StatusBlock.Text = "Add a bus tag first."; return; }

        // Open a picker listing the DBC messages of the current bus tag.
        var picker = new MessagePickerDialog(_vm, _currentBusFile.BusTag) { Owner = this };
        if (picker.ShowDialog() != true) return;

        var msg = new MtcMessage
        {
            ID   = picker.ResultId,
            Name = picker.ResultName,
            CycleTime = 100,
        };

        // Auto-fill signals from the DBC if the message was found there.
        var dbcMsg = _vm.LookupDbcMessage(_currentBusFile.BusTag, picker.ResultName);
        if (dbcMsg == null && !string.IsNullOrEmpty(picker.ResultId))
            dbcMsg = _vm.LookupDbcMessage(_currentBusFile.BusTag, picker.ResultId);

        if (dbcMsg != null)
        {
            msg.ID = $"0x{dbcMsg.Id:X3}";
            msg.Name = dbcMsg.Name;
            foreach (var s in dbcMsg.Signals)
                msg.SignalItem.Add(new MtcSignal { CanSignalName = s.Name, Value = "0", Type = 1 });
            StatusBlock.Foreground = System.Windows.Media.Brushes.DarkGreen;
            StatusBlock.Text = $"Added {dbcMsg.Name} with {dbcMsg.Signals.Count} signal(s) from DBC.";
        }
        else
        {
            StatusBlock.Foreground = System.Windows.Media.Brushes.DarkRed;
            StatusBlock.Text = $"'{picker.ResultName}' not found in DBC — added empty. Add signals manually.";
        }

        _currentBusFile.Data.Add(msg);
        _messages.Add(msg);
        MessagesGrid.SelectedItem = msg;
    }

    private void OnAddSignal(object sender, RoutedEventArgs e)
    {
        if (MessagesGrid.SelectedItem is not MtcMessage msg)
        { StatusBlock.Text = "Select a message first."; return; }
        var sig = new MtcSignal { CanSignalName = "NewSignal", Value = "0", Type = 1 };
        msg.SignalItem.Add(sig);
        _signals.Add(sig);
    }

    // ----- save ---------------------------------------------------------------

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Commit any in-progress grid edits.
        MessagesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        SignalsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var name = TcNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { StatusBlock.Text = "Enter a testcase name."; return; }

        _tc.Name = name;
        var path = Path.Combine(TestCaseFolder.Root, name + ".mtc");

        try
        {
            MtcStore.Save(_tc, path);
            _tc.FilePath = path;
            StatusBlock.Foreground = System.Windows.Media.Brushes.DarkGreen;
            StatusBlock.Text = $"Saved to {path}";
        }
        catch (Exception ex)
        {
            StatusBlock.Foreground = System.Windows.Media.Brushes.DarkRed;
            StatusBlock.Text = "Save failed: " + ex.Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
