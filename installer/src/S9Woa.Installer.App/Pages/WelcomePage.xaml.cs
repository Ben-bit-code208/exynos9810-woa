// SPDX-License-Identifier: BSD-2-Clause-Patent
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace S9Woa.Installer.App.Pages;

public sealed partial class WelcomePage : Page
{
    public WelcomePage()
    {
        InitializeComponent();
        AcceptBox.IsChecked = AppServices.RisksAccepted;
    }

    private void OnAcceptChanged(object sender, RoutedEventArgs e)
    {
        AppServices.RisksAccepted = AcceptBox.IsChecked == true;
        ContinueButton.IsEnabled = AppServices.RisksAccepted;
    }

    private void OnContinue(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("host");
}
