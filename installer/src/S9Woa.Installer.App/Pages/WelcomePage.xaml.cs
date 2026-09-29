// SPDX-License-Identifier: BSD-2-Clause-Patent
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace S9Woa.Installer.App.Pages;

public sealed partial class WelcomePage : Page, IWizardStep
{
    public WelcomePage()
    {
        InitializeComponent();
        AcceptBox.IsChecked = AppServices.RisksAccepted;
    }

    public event EventHandler? StateChanged;

    public bool CanAdvance => AppServices.RisksAccepted;

    public string? NextLabel => "Get started";

    private void OnAcceptChanged(object sender, RoutedEventArgs e)
    {
        AppServices.RisksAccepted = AcceptBox.IsChecked == true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
