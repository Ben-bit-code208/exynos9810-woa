// SPDX-License-Identifier: BSD-2-Clause-Patent
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using S9Woa.Installer.Core.Image;

namespace S9Woa.Installer.App.Pages;

public sealed partial class ImagePage : Page
{
    private static readonly string[] MediaExtensions = [".iso", ".esd", ".wim"];

    public ImagePage()
    {
        InitializeComponent();
        MediaBox.Text = AppServices.MediaPath ?? "";
        ProfileChoice.SelectedIndex = (int)AppServices.Profile;
        UpdateProfile();
        Validate();
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(App.Window!.AppWindow.Id);
        foreach (var ext in MediaExtensions)
        {
            picker.FileTypeFilter.Add(ext);
        }
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            MediaBox.Text = file.Path;
        }
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e) => Validate();

    private void OnMediaTextChanged(object sender, TextChangedEventArgs e) => Validate();

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateProfile();
        AppServices.SaveState();
    }

    private void UpdateProfile()
    {
        if (ProfileChoice.SelectedItem is RadioButton { Tag: string tag } && Enum.TryParse<SlimProfile>(tag, out var p))
        {
            AppServices.Profile = p;
        }
        ProfileSummary.Text = SlimPlan.Summary(AppServices.Profile);
        CoreWarning.IsOpen = AppServices.Profile == SlimProfile.Core;
    }

    private void Validate()
    {
        var path = MediaBox.Text.Trim().Trim('"');
        var ok = path.Length > 0 && File.Exists(path)
            && MediaExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        ContinueButton.IsEnabled = ok;
        if (ok)
        {
            AppServices.MediaPath = path;
            AppServices.SaveState();
        }
    }

    private void OnContinue(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("install");

    private void OnAccountChanged(object sender, RoutedEventArgs e)
    {
        var name = AccountName.Text.Trim();
        AppServices.Unattend = AppServices.Unattend with
        {
            Username = name.Length == 0 ? "S9" : name,
            Password = AccountPassword.Password.Length == 0 ? null : AccountPassword.Password,
        };
    }
}
