// ============================================================================
// AddMessageDialog.xaml.cs
// Two ways to add a message to the loaded testcase's Send Messenger:
//   ① type an ID ("0x488") or a name ("ACM_WarnMsg")
//   ② pick from the list of DBC messages across configured buses
// Both call MainViewModel.AddSendMessage, which fills signals from the DBC.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CanTracer.ViewModels;

namespace CanTracer.Views;

public partial class AddMessageDialog : Window
{
    private readonly MainViewModel _vm;
    private readonly List<string> _allNames;

    public AddMessageDialog(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _allNames = _vm.AvailableDbcMessageNames().ToList();
        DbcList.ItemsSource = _allNames;
    }

    private void OnAddTyped(object sender, RoutedEventArgs e)
        => DoAdd(IdNameBox.Text);

    private void OnAddSelected(object sender, RoutedEventArgs e)
    {
        if (DbcList.SelectedItem is string s)
            DoAdd(ExtractName(s));
        else
            StatusBlock.Text = "Select a message from the list first.";
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DbcList.SelectedItem is string s)
            DoAdd(ExtractName(s));
    }

    private void DoAdd(string idOrName)
    {
        var err = _vm.AddSendMessage(idOrName);
        if (err == null)
        {
            StatusBlock.Foreground = System.Windows.Media.Brushes.DarkGreen;
            StatusBlock.Text = $"Added '{idOrName}'.";
        }
        else
        {
            StatusBlock.Foreground = System.Windows.Media.Brushes.DarkRed;
            StatusBlock.Text = err;
        }
    }

    // "ACM_WarnMsg  (0x488)" → "ACM_WarnMsg"
    private static string ExtractName(string listItem)
    {
        var idx = listItem.IndexOf("  (", StringComparison.Ordinal);
        return idx > 0 ? listItem.Substring(0, idx) : listItem;
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var f = FilterBox.Text.Trim();
        DbcList.ItemsSource = string.IsNullOrEmpty(f)
            ? _allNames
            : _allNames.Where(n => n.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
