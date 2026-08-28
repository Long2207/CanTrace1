using System.Windows;
using System.Windows.Controls;
using CanTracer.Models;
using Microsoft.Win32;

namespace CanTracer.Views;

public partial class AddBusDialog : Window
{
    private const string DefaultFdBitrate =
        "f_clock_mhz=80, nom_brp=2, nom_tseg1=63, nom_tseg2=16, nom_sjw=16, " +
        "data_brp=2, data_tseg1=15, data_tseg2=4, data_sjw=4";

    public CanBus? Result { get; private set; }

    public AddBusDialog()
    {
        InitializeComponent();
        NameBox.Text = "BCAN";
        FdBox2.Text = DefaultFdBitrate;
    }

    private void OnFdToggled(object sender, RoutedEventArgs e)
    {
        if (BaudLabel is null) return;       // event can fire during InitializeComponent
        var fd = FdBox.IsChecked == true;
        BaudLabel.Visibility = BaudBox.Visibility = fd ? Visibility.Collapsed : Visibility.Visible;
        FdLabel.Visibility   = FdBox2.Visibility = fd ? Visibility.Visible   : Visibility.Collapsed;
    }

    private void OnBrowseDbc(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "DBC files (*.dbc)|*.dbc|All files (*.*)|*.*",
            Title = "Open DBC file for this bus"
        };
        if (dlg.ShowDialog() == true) DbcBox.Text = dlg.FileName;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        StatusBlock.Text = "";
        var name = (NameBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name)) { StatusBlock.Text = "Name is required."; return; }

        var fd = FdBox.IsChecked == true;
        Result = new CanBus
        {
            Name        = name,
            ChannelId = (ChannelBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "PCAN_USBBUS1",
            IsFd        = fd,
            Baud        = (BaudBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "500 kbit/s",
            FdData      = fd ? FdBox2.Text.Trim() : "",
            DbcPath     = DbcBox.Text,
        };
        DialogResult = true;
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
