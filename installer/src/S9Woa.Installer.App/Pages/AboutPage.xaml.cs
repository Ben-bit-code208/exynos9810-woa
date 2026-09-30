// SPDX-License-Identifier: BSD-2-Clause-Patent
using Microsoft.UI.Xaml.Controls;

namespace S9Woa.Installer.App.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        var version = typeof(AboutPage).Assembly.GetName().Version;
        VersionText.Text = AppServices.Redact($"Version {version?.ToString(3)} · logs in {AppServices.LogDirectory}");
    }
}
