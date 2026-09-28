// SPDX-License-Identifier: BSD-2-Clause-Patent
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using S9Woa.Installer.App.Pages;

namespace S9Woa.Installer.App;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["welcome"] = typeof(WelcomePage),
        ["setup"] = typeof(SetupPage),
        ["host"] = typeof(HostPage),
        ["phone"] = typeof(PhonePage),
        ["image"] = typeof(ImagePage),
        ["install"] = typeof(InstallPage),
        ["tools"] = typeof(ToolsPage),
        ["about"] = typeof(AboutPage),
    };

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 820));

        var complete = Core.Toolset.ToolsetManager.IsComplete(AppServices.Toolset.DetectAll());
        ApplySetupGate(complete);
        // First run starts at Welcome; afterwards, a broken toolset goes straight to Setup.
        Nav.SelectedItem = !complete && AppServices.Toolset.Config.SetupCompleted ? SetupItem : WelcomeItem;
    }

    /// <summary>True once every required tool is ready; the install flow stays locked until then.</summary>
    public bool SetupComplete { get; private set; }

    public void ApplySetupGate(bool complete)
    {
        SetupComplete = complete;
        foreach (var item in new[] { HostItem, PhoneItem, ImageItem, InstallItem })
        {
            item.IsEnabled = complete;
        }
    }

    public void NavigateTo(string tag)
    {
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>()
            .First(i => (string)i.Tag == tag);
        if (!item.IsEnabled)
        {
            item = SetupItem;
        }
        Nav.SelectedItem = item;
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && Pages.TryGetValue(tag, out var page)
            && ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
        }
    }
}
