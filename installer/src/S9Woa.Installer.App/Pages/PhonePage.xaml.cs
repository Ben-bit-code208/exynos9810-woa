// SPDX-License-Identifier: BSD-2-Clause-Patent
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using S9Woa.Installer.Core;
using S9Woa.Installer.Core.Device;
using S9Woa.Installer.Core.Stages;

namespace S9Woa.Installer.App.Pages;

public sealed partial class PhonePage : Page
{
    private readonly DispatcherQueueTimer _timer;
    private bool _polling;
    private string? _lastKey;

    public PhonePage()
    {
        InitializeComponent();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick += async (_, _) => await PollAsync();
        Loaded += async (_, _) => { _timer.Start(); await PollAsync(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private async Task PollAsync()
    {
        if (_polling)
        {
            return;
        }
        _polling = true;
        try
        {
            if (AppServices.Adb is null)
            {
                Show(InfoBarSeverity.Error, "adb.exe not found", "Install Android platform tools on the This PC page.");
                return;
            }
            var devices = await AppServices.Adb.ListDevicesAsync();
            if (devices.Count == 0)
            {
                AppServices.CurrentDevice = null;
                DeviceCard.Visibility = Visibility.Collapsed;
                ContinueButton.IsEnabled = false;
                Show(InfoBarSeverity.Informational, "Looking for a phone…", "Connect the phone with USB debugging enabled.");
                return;
            }
            if (devices.Count > 1)
            {
                Show(InfoBarSeverity.Warning, "More than one Android device", "Disconnect every device except the phone you want to install Windows on.");
                ContinueButton.IsEnabled = false;
                return;
            }

            var dev = devices[0];
            var props = dev.State is AdbState.Device or AdbState.Recovery
                ? await AppServices.Adb.GetPropertiesAsync(dev.Serial)
                : null;
            var snap = DeviceSnapshot.FromAdb(dev, props);
            var results = DeviceEligibility.Evaluate(snap);
            AppServices.CurrentDevice = snap;

            var key = $"{snap.Serial}|{snap.Mode}|{snap.Bootloader}|{snap.FlashLocked}";
            if (key != _lastKey)
            {
                _lastKey = key;
                AppServices.Log($"device {snap.Mode} model={snap.Model} bl={snap.Bootloader} locked={snap.FlashLocked}");
            }

            DeviceCard.Visibility = Visibility.Visible;
            DeviceTitle.Text = snap.Model is null ? "Android device" : $"Samsung {snap.Model}";
            DeviceDetail.Text = snap.Mode switch
            {
                DeviceMode.Recovery => $"In TWRP {snap.RecoveryVersion} · firmware {snap.Bootloader ?? "unknown"}",
                DeviceMode.Android => $"Android {snap.AndroidVersion} · firmware {snap.Bootloader ?? "unknown"}",
                _ => snap.Mode.ToString(),
            };
            Results.ItemsSource = results.Select(r => new CheckItem(r)).ToList();

            var blocked = results.HasBlockers();
            ContinueButton.IsEnabled = !blocked;
            if (blocked)
            {
                Show(InfoBarSeverity.Error, "This phone can't be used yet", "Resolve the items marked in red below.");
            }
            else
            {
                Show(InfoBarSeverity.Success, "Phone ready", "Your phone passed the compatibility checks.");
            }
            AppServices.State.Set("identify", blocked ? StageStatus.Failed : StageStatus.Done);
            if (snap.FlashLocked == false)
            {
                AppServices.State.Set("unlock", StageStatus.Done);
            }
            AppServices.SaveState();
        }
        catch (Exception e)
        {
            Show(InfoBarSeverity.Warning, "Couldn't talk to the phone", e.Message);
        }
        finally
        {
            _polling = false;
        }
    }

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
    }

    private Task RebootAsync(RebootTarget target) => DeviceCommands.RebootAsync(XamlRoot, target);

    private async void OnRebootRecovery(object sender, RoutedEventArgs e) => await RebootAsync(RebootTarget.Recovery);

    private async void OnRebootDownload(object sender, RoutedEventArgs e) => await RebootAsync(RebootTarget.Download);

    private async void OnRebootSystem(object sender, RoutedEventArgs e) => await RebootAsync(RebootTarget.System);

    private void OnContinue(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("image");
}
