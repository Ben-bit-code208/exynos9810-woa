// SPDX-License-Identifier: BSD-2-Clause-Patent
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using S9Woa.Installer.Core;
using S9Woa.Installer.Core.Host;
using S9Woa.Installer.Core.Stages;

namespace S9Woa.Installer.App.Pages;

public sealed partial class HostPage : Page
{
    public HostPage()
    {
        InitializeComponent();
        WorkDirBox.Text = AppServices.WorkDirectory;
        Loaded += (_, _) => Evaluate();
    }

    private void Evaluate()
    {
        AppServices.WorkDirectory = WorkDirBox.Text.Trim();
        var results = HostPreflight.Evaluate(new LocalHostEnvironment(AppServices.AdbPath), AppServices.WorkDirectory);
        Results.ItemsSource = results.Select(r => new CheckItem(r)).ToList();
        var ok = !results.HasBlockers();
        ContinueButton.IsEnabled = ok;
        AppServices.State.Set("host", ok ? StageStatus.Done : StageStatus.Failed);
        AppServices.SaveState();
        foreach (var r in results)
        {
            AppServices.Log($"host {r.Id} {r.Severity}: {r.Detail}");
        }
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(App.Window!.AppWindow.Id);
        var result = await picker.PickSingleFolderAsync();
        if (result is not null)
        {
            WorkDirBox.Text = Path.Combine(result.Path, "S9WoaInstaller");
            Evaluate();
        }
    }

    private void OnRecheck(object sender, RoutedEventArgs e) => Evaluate();

    private void OnContinue(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("phone");
}
