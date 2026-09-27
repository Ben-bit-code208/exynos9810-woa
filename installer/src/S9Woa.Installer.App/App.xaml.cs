// SPDX-License-Identifier: BSD-2-Clause-Patent
using Microsoft.UI.Xaml;

namespace S9Woa.Installer.App;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => AppServices.Log($"Unhandled: {e.Exception}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        Window.Activate();
    }
}
