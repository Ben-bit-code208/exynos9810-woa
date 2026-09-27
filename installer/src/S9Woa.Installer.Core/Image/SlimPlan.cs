// SPDX-License-Identifier: BSD-2-Clause-Patent
namespace S9Woa.Installer.Core.Image;

/// <summary>
/// Builds the ordered list of image modifications for a <see cref="SlimProfile"/>.
/// The approach follows the idea popularised by tiny11builder (remove inbox packages
/// and apply offline registry defaults with DISM), implemented independently here.
/// Drivers must be injected before a plan runs; a Core plan ends in /ResetBase.
/// </summary>
public static class SlimPlan
{
    internal static readonly string[] ConsumerApps =
    [
        "Clipchamp.Clipchamp_",
        "Microsoft.BingNews_",
        "Microsoft.BingSearch_",
        "Microsoft.BingWeather_",
        "Microsoft.GamingApp_",
        "Microsoft.GetHelp_",
        "Microsoft.Getstarted_",
        "Microsoft.MicrosoftOfficeHub_",
        "Microsoft.MicrosoftSolitaireCollection_",
        "Microsoft.MicrosoftStickyNotes_",
        "Microsoft.OutlookForWindows_",
        "Microsoft.People_",
        "Microsoft.PowerAutomateDesktop_",
        "Microsoft.Todos_",
        "Microsoft.WindowsAlarms_",
        "Microsoft.WindowsCamera_",
        "Microsoft.WindowsFeedbackHub_",
        "Microsoft.WindowsMaps_",
        "Microsoft.WindowsSoundRecorder_",
        "Microsoft.Xbox.TCUI_",
        "Microsoft.XboxGameOverlay_",
        "Microsoft.XboxGamingOverlay_",
        "Microsoft.XboxIdentityProvider_",
        "Microsoft.XboxSpeechToTextOverlay_",
        "Microsoft.YourPhone_",
        "Microsoft.ZuneMusic_",
        "Microsoft.ZuneVideo_",
        "MicrosoftCorporationII.MicrosoftFamily_",
        "MicrosoftCorporationII.QuickAssist_",
        "MicrosoftTeams_",
        "MSTeams_",
        "microsoft.windowscommunicationsapps_",
    ];

    internal static readonly string[] CoreCapabilities =
    [
        "App.StepsRecorder~",
        "Browser.InternetExplorer~",
        "Hello.Face.",
        "Language.Handwriting~",
        "Language.OCR~",
        "Language.Speech~",
        "Language.TextToSpeech~",
        "MathRecognizer~",
        "Media.WindowsMediaPlayer~",
        "Microsoft.Windows.WordPad~",
        "OneCoreUAP.OneSync~",
        "Print.Fax.Scan~",
    ];

    /// <summary>Paths no plan may delete: kernel, loader, boot and driver stores.</summary>
    private static readonly string[] ProtectedPrefixes =
    [
        @"EFI\",
        @"Boot\",
        @"Windows\Boot\",
        @"Windows\System32\drivers\",
        @"Windows\System32\DriverStore\",
        @"Windows\System32\config\",
        @"Windows\System32\CodeIntegrity\",
        @"Windows\WinSxS\",
    ];

    private static readonly string[] ProtectedFiles =
    [
        @"Windows\System32\ntoskrnl.exe",
        @"Windows\System32\hal.dll",
        @"Windows\System32\winload.efi",
        @"Windows\System32\winload.exe",
        @"Windows\System32\ci.dll",
        @"bootmgr",
    ];

    public static bool IsProtectedPath(string relativePath)
    {
        var p = relativePath.Replace('/', '\\').TrimStart('\\');
        if (p.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(p))
        {
            return true;
        }
        return ProtectedPrefixes.Any(x => p.StartsWith(x, StringComparison.OrdinalIgnoreCase))
            || ProtectedFiles.Any(x => p.Equals(x, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<SlimOperation> For(SlimProfile profile)
    {
        var ops = new List<SlimOperation>();
        if (profile == SlimProfile.None)
        {
            return ops;
        }

        ops.AddRange(ConsumerApps.Select(a => new RemoveProvisionedAppx(a)));
        ops.AddRange(LiteRegistry());

        if (profile == SlimProfile.Core)
        {
            ops.AddRange(CoreCapabilities.Select(c => new RemoveCapability(c)));
            ops.Add(new DisableFeature("Recall"));
            ops.AddRange(CoreRegistry());
            ops.Add(new DeleteImageFile(@"Windows\System32\Recovery\winre.wim"));
            ops.Add(new ComponentCleanup(ResetBase: true));
        }
        else
        {
            ops.Add(new ComponentCleanup(ResetBase: false));
        }

        foreach (var op in ops.OfType<DeleteImageFile>())
        {
            if (IsProtectedPath(op.RelativePath))
            {
                throw new InvalidOperationException($"Plan would delete protected path {op.RelativePath}.");
            }
        }
        return ops;
    }

    private static IEnumerable<SlimOperation> LiteRegistry()
    {
        const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        const string Policies = @"Policies\Microsoft\Windows";

        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0);
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1);
        yield return Dword(RegistryHive.Software, $@"{Policies}\Windows Chat", "ChatIcon", 3);
        yield return Dword(RegistryHive.Software, @"Microsoft\Windows\CurrentVersion\OOBE", "BypassNRO", 1);
        yield return Dword(RegistryHive.Software, @"Microsoft\PolicyManager\current\device\Start", "ConfigureStartPins", 0);
        foreach (var name in new[]
                 {
                     "ContentDeliveryAllowed", "FeatureManagementEnabled", "OemPreInstalledAppsEnabled",
                     "PreInstalledAppsEnabled", "PreInstalledAppsEverEnabled", "SilentInstalledAppsEnabled",
                     "SoftLandingEnabled", "SubscribedContentEnabled", "SystemPaneSuggestionsEnabled",
                 })
        {
            yield return Dword(RegistryHive.DefaultUser, Cdm, name, 0);
        }
        yield return Dword(RegistryHive.DefaultUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0);
        yield return Dword(RegistryHive.DefaultUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarMn", 0);
    }

    private static IEnumerable<SlimOperation> CoreRegistry()
    {
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1);
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 1);
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\Windows Defender\Spynet", "SpynetReporting", 0);
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\Windows Defender\Spynet", "SubmitSamplesConsent", 2);
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\MRT", "DontOfferThroughWUAU", 1);
        foreach (var svc in new[] { "WinDefend", "WdNisSvc", "SecurityHealthService", "Sense", "WdBoot", "WdFilter", "WdNisDrv" })
        {
            yield return Dword(RegistryHive.System, $@"ControlSet001\Services\{svc}", "Start", 4);
        }
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", 1);
    }

    private static SetRegistryValue Dword(RegistryHive hive, string key, string name, uint value) =>
        new(hive, key, name, RegistryValueKind.DWord, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static string Summary(SlimProfile profile) => profile switch
    {
        SlimProfile.None => "Stock Windows with the phone drivers added.",
        SlimProfile.Lite => "Removes consumer inbox apps, ads and telemetry defaults, and allows a local account in OOBE. Windows Update keeps working.",
        SlimProfile.Core => "Lite plus removal of Defender, WinRE, optional capabilities and the component store backup. Smallest and fastest, but it can no longer be updated or repaired and has no antivirus.",
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };
}
