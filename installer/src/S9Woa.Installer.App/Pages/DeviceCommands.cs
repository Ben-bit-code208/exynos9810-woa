// SPDX-License-Identifier: BSD-2-Clause-Patent
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using S9Woa.Installer.Core.Device;

namespace S9Woa.Installer.App.Pages;

/// <summary>Shared device actions used by the Phone and Device tools pages.</summary>
internal static class DeviceCommands
{
    public static async Task RebootAsync(XamlRoot root, RebootTarget target)
    {
        var snap = AppServices.CurrentDevice;
        if (AppServices.Device is null || snap is null)
        {
            await Message(root, "No phone connected", "Connect the phone over USB with debugging enabled.");
            return;
        }
        if (target == RebootTarget.Recovery && snap.Mode == DeviceMode.Android && snap.FlashLocked != false)
        {
            await Message(root, "TWRP isn't installed yet",
                "The bootloader is still locked, so this would start Samsung's stock recovery instead of TWRP.");
            return;
        }
        try
        {
            AppServices.Log($"reboot {target} ({snap.Serial})");
            await AppServices.Device.RebootAsync(snap.Serial, target);
        }
        catch (Exception e)
        {
            await Message(root, "Reboot failed", e.Message);
        }
    }

    public static async Task Message(XamlRoot root, string title, string text)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = AppServices.Redact(text), TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }
}
