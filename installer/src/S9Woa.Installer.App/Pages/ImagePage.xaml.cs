// SPDX-License-Identifier: BSD-2-Clause-Patent
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using S9Woa.Installer.Core.Image;

namespace S9Woa.Installer.App.Pages;

public sealed partial class ImagePage : Page, IWizardStep
{
    private static readonly string[] MediaExtensions = [".iso", ".esd", ".wim"];
    private bool _ok;

    public ImagePage()
    {
        InitializeComponent();
        MediaBox.Text = AppServices.MediaPath ?? "";
        (AppServices.Profile switch
        {
            SlimProfile.None => StockOption,
            SlimProfile.Core => CoreOption,
            _ => LiteOption,
        }).IsChecked = true;
        AccountPassword.Password = AppServices.Unattend.Password ?? "";
        AccountName.Text = AppServices.Unattend.Username;
        UpdateProfile();
        Validate();
    }

    public event EventHandler? StateChanged;

    public bool CanAdvance => _ok;

    public string? NextLabel => "Review";

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

    private void OnMediaTextChanged(object sender, TextChangedEventArgs e) => Validate();

    private void OnProfileChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse<SlimProfile>(tag, out var p))
        {
            AppServices.Profile = p;
            AppServices.SaveState();
        }
        UpdateProfile();
    }

    private void UpdateProfile()
    {
        // The Checked handler can run during InitializeComponent, before every card exists.
        if (StockCard is null || LiteCard is null || CoreCard is null || ProfileSummary is null)
        {
            return;
        }
        var accent = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var normal = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        StockCard.BorderBrush = AppServices.Profile == SlimProfile.None ? accent : normal;
        LiteCard.BorderBrush = AppServices.Profile == SlimProfile.Lite ? accent : normal;
        CoreCard.BorderBrush = AppServices.Profile == SlimProfile.Core ? accent : normal;
        ProfileSummary.Text = SlimPlan.Summary(AppServices.Profile);
        CoreWarning.IsOpen = AppServices.Profile == SlimProfile.Core;
    }

    private void Validate()
    {
        var path = MediaBox.Text.Trim().Trim('"');
        var ok = path.Length > 0 && File.Exists(path)
            && MediaExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        if (ok)
        {
            AppServices.MediaPath = path;
            AppServices.SaveState();
        }
        if (ok != _ok)
        {
            _ok = ok;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

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
