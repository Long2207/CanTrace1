using System.Windows;
using CanTracer.ViewModels;
using CanTracer.Views;

namespace CanTracer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.RequestSettingsDialog   += OpenSettings;
        _vm.RequestSendByDbcDialog  += OpenSendByDbc;
        _vm.RequestTestCaseEditor   += OpenTestCaseEditor;
        _vm.RequestAddMessageDialog += OpenAddMessage;
        _vm.Cyclic.JobsChanged      += RefreshCyclicPanel;
        RefreshCyclicPanel();
    }

    private void OpenAddMessage()
    {
        var dlg = new AddMessageDialog(_vm) { Owner = this };
        dlg.ShowDialog();
    }

    private void OpenSettings()
    {
        var dlg = new SettingsDialog(_vm.Buses) { Owner = this };
        // Per-row Connect button: connect + auto-start that bus without closing.
        dlg.ConnectRequested += bus => _vm.ConnectSingleBusFromSettings(bus);
        if (dlg.ShowDialog() == true && dlg.Result != null)
            _vm.ApplySettings(dlg.Result);
    }

    private void OpenTestCaseEditor(CanTracer.Models.TestCaseFile? file)
    {
        var dlg = new TestCaseEditorDialog(_vm, file) { Owner = this };
        dlg.ShowDialog();
        _vm.RefreshTestCases();   // pick up any new/edited .mtc
    }

    private void OpenSendByDbc()
    {
        new SendByDbcDialog(_vm) { Owner = this }.ShowDialog();
    }

    private void RefreshCyclicPanel()
    {
        Dispatcher.Invoke(() =>
        {
            var jobs = _vm.Cyclic.Jobs;
            CyclicJobsView.ItemsSource = jobs;
            CyclicPanel.Visibility = jobs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void OnWindowClosed(object? sender, System.EventArgs e)
    {
        _vm.Cyclic.Dispose();
    }
}
