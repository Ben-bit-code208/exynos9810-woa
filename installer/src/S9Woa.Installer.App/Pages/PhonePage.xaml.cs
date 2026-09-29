// SPDX-License-Identifier: BSD-2-Clause-Patent
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using S9Woa.Installer.Core;
using S9Woa.Installer.Core.Device;
using S9Woa.Installer.Core.Stages;

namespace S9Woa.Installer.App.Pages;

public sealed partial class PhonePage : Page, IWizardStep
{
    private readonly DispatcherQueueTimer _timer;
    private bool _polling;
    private bool _ready;
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

    public event EventHandler? StateChanged;

    public bool CanAdvance => _ready;

    private void SetReady(bool ready)
    {
        if (_ready != ready)
        {
            _ready = ready;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ShowSearching(string title, string detail)
    {
        SearchRing.IsActive = true;
        DeviceIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        DeviceTitle.Text = title;
        DeviceDetail.Text = detail;
        RebootMenu.Visibility = Visibility.Collapsed;
        ChecksCard.Visibility = Visibility.Collapsed;
        StatusBar.IsOpen = false;
        SetReady(false);
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
                ShowSearching("adb isn't set up", "Go back to Set up and install the Android platform tools.");
                return;
            }
            var devices = await AppServices.Adb.ListDevicesAsync();
            if (devices.Count == 0)
            {
                AppServices.CurrentDevice = null;
                ShowSearching("Looking for your phone…", "Connect it with USB debugging turned on.");
                return;
            }
            if (devices.Count > 1)
            {
                ShowSearching("More than one Android device", "Disconnect everything except the phone you want to install Windows on.");
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

            SearchRing.IsActive = false;
            DeviceIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            DeviceTitle.Text = snap.Model switch
            {
                null => "Android device",
                var m when m.Contains("G965", StringComparison.OrdinalIgnoreCase) => $"Galaxy S9+ · {m}",
                var m => m,
            };
            DeviceDetail.Text = snap.Mode switch
            {
                DeviceMode.Recovery => $"In TWRP {snap.RecoveryVersion} · firmware {snap.Bootloader ?? "unknown"}",
                DeviceMode.Android => $"Android {snap.AndroidVersion} · firmware {snap.Bootloader ?? "unknown"}",
                DeviceMode.Unauthorized => "Waiting for you to tap Allow on the phone",
                _ => snap.Mode.ToString(),
            };
            RebootMenu.Visibility = snap.Mode is DeviceMode.Android or DeviceMode.Recovery ? Visibility.Visible : Visibility.Collapsed;
            Results.ItemsSource = results.Select(r => new CheckItem(r)).ToList();
            ChecksCard.Visibility = Visibility.Visible;

            var blocked = results.HasBlockers();
            if (blocked)
            {
                Show(InfoBarSeverity.Error, "This phone can't be used yet", "Resolve the items marked in red below.");
            }
            else
            {
                StatusBar.IsOpen = false;
            }
            SetReady(!blocked);
            AppServices.State.Set("identify", blocked ? StageStatus.Failed : StageStatus.Done);
            if (snap.FlashLocked == false)
            {
                AppServices.State.Set("unlock", StageStatus.Done);
            }
            AppServices.SaveState();
        }
        catch (Exception e)
        {
            ShowSearching("Couldn't talk to the phone", e.Message);
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
        StatusBar.IsOpen = true;
    }

    private Task RebootAsync(RebootTarget target) => DeviceCommands.RebootAsync(XamlRoot, target);

    private async void OnRebootRecovery(object sender, RoutedEventArgs e) => await RebootAsync(RebootTarget.Recovery);

    private async void OnRebootDownload(object sender, RoutedEventArgs e) => await RebootAsync(RebootTarget.Download);

    private async void OnRebootSystem(object sender, RoutedEventArgs e) => await RebootAsync(RebootTarget.System);
}
