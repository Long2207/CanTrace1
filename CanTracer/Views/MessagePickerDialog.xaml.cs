// ============================================================================
// MessagePickerDialog.xaml.cs
// Lets the testcase editor pick a message for a given bus tag, either by typing
// an ID/name or by selecting from that bus's DBC message list. The caller reads
// ResultId / ResultName after a true DialogResult.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CanTracer.ViewModels;

namespace CanTracer.Views;

public partial class MessagePickerDialog : Window
{
    private readonly List<string> _allNames;

    public string ResultId { get; private set; } = "";
    public string ResultName { get; private set; } = "";

    public MessagePickerDialog(MainViewModel vm, string busTag)
    {
        InitializeComponent();
        HeaderText.Text = $"Bus tag: {busTag}";
        _allNames = vm.DbcMessageNamesForTag(busTag).ToList();
        DbcList.ItemsSource = _allNames;

        if (_allNames.Count == 0)
            HeaderText.Text = $"Bus tag: {busTag} — no DBC loaded for this bus. " +
                              "You can still type a name/ID manually.";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // List selection wins; otherwise use the manual box.
        if (DbcList.SelectedItem is string s)
        {
            SetResultFromListItem(s);
        }
        else if (!string.IsNullOrWhiteSpace(ManualBox.Text))
        {
            var t = ManualBox.Text.Trim();
            // If they typed "0x..." treat as ID, else as name.
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) ResultId = t;
            else ResultName = t;
        }
        else
        {
            return;   // nothing chosen
        }
        DialogResult = true;
        Close();
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DbcList.SelectedItem is string s)
        {
            SetResultFromListItem(s);
            DialogResult = true;
            Close();
        }
    }

    // "ACM_WarnMsg  (0x488)" → name + id
    private void SetResultFromListItem(string item)
    {
        var idx = item.IndexOf("  (", StringComparison.Ordinal);
        if (idx > 0)
        {
            ResultName = item.Substring(0, idx);
            var rest = item.Substring(idx + 3).TrimEnd(')');   // "0x488"
            ResultId = rest;
        }
        else ResultName = item;
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var f = FilterBox.Text.Trim();
        DbcList.ItemsSource = string.IsNullOrEmpty(f)
            ? _allNames
            : _allNames.Where(n => n.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
