// SPDX-License-Identifier: BSD-2-Clause-Patent
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using S9Woa.Installer.Core;
using S9Woa.Installer.Core.Host;
using S9Woa.Installer.Core.Stages;

namespace S9Woa.Installer.App.Pages;

public sealed partial class HostPage : Page, IWizardStep
{
    private bool _ok;

    public HostPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Evaluate();
    }

    public event EventHandler? StateChanged;

    public bool CanAdvance => _ok;

    private void Evaluate()
    {
        WorkDirText.Text = AppServices.WorkDirectory;
        var results = HostPreflight.Evaluate(new LocalHostEnvironment(AppServices.AdbPath), AppServices.WorkDirectory);
        Results.ItemsSource = results.Select(r => new CheckItem(r)).ToList();
        _ok = !results.HasBlockers();
        AppServices.State.Set("host", _ok ? StageStatus.Done : StageStatus.Failed);
        AppServices.SaveState();
        foreach (var r in results)
        {
            AppServices.Log($"host {r.Id} {r.Severity}: {r.Detail}");
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(App.Window!.AppWindow.Id);
        var result = await picker.PickSingleFolderAsync();
        if (result is not null)
        {
            AppServices.WorkDirectory = Path.Combine(result.Path, "S9WoaInstaller");
            Evaluate();
        }
    }

    private void OnRecheck(object sender, RoutedEventArgs e) => Evaluate();
}
