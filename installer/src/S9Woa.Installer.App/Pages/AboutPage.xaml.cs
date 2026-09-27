// SPDX-License-Identifier: BSD-2-Clause-Patent
using Microsoft.UI.Xaml.Controls;

namespace S9Woa.Installer.App.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"Version {typeof(AboutPage).Assembly.GetName().Version} · logs in {AppServices.LogDirectory}";
    }
}
