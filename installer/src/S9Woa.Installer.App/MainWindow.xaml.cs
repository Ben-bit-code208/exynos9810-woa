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
        Nav.SelectedItem = WelcomeItem;
    }

    public void NavigateTo(string tag)
    {
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>()
            .First(i => (string)i.Tag == tag);
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
