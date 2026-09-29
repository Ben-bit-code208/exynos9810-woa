// SPDX-License-Identifier: BSD-2-Clause-Patent
namespace S9Woa.Installer.Core.Image;

/// <summary>
/// Builds the ordered list of image modifications for a <see cref="SlimProfile"/>.
/// Lite follows the idea popularised by tiny11builder (remove inbox apps and apply offline
/// registry defaults). Core follows tiny11Coremaker.ps1 from the same project: it also removes
/// packages, Edge/WebView2/OneDrive/WinRE, cuts WinSxS down to the servicing stack and turns off
/// Windows Update and Defender. Drivers must be injected before a plan runs.
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

    /// <summary>Extra inbox apps tiny11 Core removes on top of <see cref="ConsumerApps"/>.</summary>
    internal static readonly string[] CoreApps =
    [
        "Microsoft.549981C3F5F10_",
        "Microsoft.Copilot_",
        "Microsoft.Windows.Copilot",
        "Microsoft.Windows.Teams_",
    ];

    /// <summary>CBS packages tiny11 Core removes (every language's language features).</summary>
    internal static readonly string[] CorePackages =
    [
        "Microsoft-Windows-InternetExplorer-Optional-Package~",
        "Microsoft-Windows-Kernel-LA57-FoD-Package~",
        "Microsoft-Windows-LanguageFeatures-Handwriting-",
        "Microsoft-Windows-LanguageFeatures-OCR-",
        "Microsoft-Windows-LanguageFeatures-Speech-",
        "Microsoft-Windows-LanguageFeatures-TextToSpeech-",
        "Microsoft-Windows-MediaPlayer-Package~",
        "Microsoft-Windows-Wallpaper-Content-Extended-FoD-Package~",
        "Windows-Defender-Client-Package~",
        "Microsoft-Windows-WordPad-FoD-Package~",
        "Microsoft-Windows-TabletPCMath-Package~",
        "Microsoft-Windows-StepsRecorder-Package~",
    ];

    /// <summary>
    /// WinSxS entries tiny11 Core keeps on ARM64: the servicing stack, store metadata and the
    /// side-by-side runtime assemblies apps bind to. The amd64 runtime assemblies are kept as
    /// well so x64 apps running under emulation still find them.
    /// </summary>
    internal static readonly string[] ComponentStoreKeep =
    [
        "Catalogs",
        "FileMaps",
        "Fusion",
        "InstallTemp",
        "Manifests",
        "SettingsManifests",
        "Temp",
        "arm64_microsoft-windows-servicingstack-onecore_31bf3856ad364e35_*",
        "arm64_microsoft-windows-servicing-adm_31bf3856ad364e35_*",
        "arm64_microsoft-windows-servicingcommon_31bf3856ad364e35_*",
        "arm64_microsoft-windows-servicing-onecore-uapi_31bf3856ad364e35_*",
        "arm64_microsoft-windows-servicingstack_31bf3856ad364e35_*",
        "arm64_microsoft-windows-servicingstack-inetsrv_31bf3856ad364e35_*",
        "arm64_microsoft-windows-servicingstack-msg_31bf3856ad364e35_*",
        "x86_microsoft.vc80.crt_1fc8b3b9a1e18e3b_*",
        "x86_microsoft.vc90.crt_1fc8b3b9a1e18e3b_*",
        "x86_microsoft.windows.c..-controls.resources_6595b64144ccf1df_*",
        "x86_microsoft.windows.common-controls_6595b64144ccf1df_*",
        "x86_microsoft.windows.gdiplus_6595b64144ccf1df_*",
        "x86_microsoft.windows.i..utomation.proxystub_6595b64144ccf1df_*",
        "x86_microsoft.windows.isolationautomation_6595b64144ccf1df_*",
        "arm_microsoft.windows.c..-controls.resources_6595b64144ccf1df_*",
        "arm_microsoft.windows.common-controls_6595b64144ccf1df_*",
        "arm_microsoft.windows.gdiplus_6595b64144ccf1df_*",
        "arm_microsoft.windows.i..utomation.proxystub_6595b64144ccf1df_*",
        "arm_microsoft.windows.isolationautomation_6595b64144ccf1df_*",
        "arm64_microsoft.vc80.crt_1fc8b3b9a1e18e3b_*",
        "arm64_microsoft.vc90.crt_1fc8b3b9a1e18e3b_*",
        "arm64_microsoft.windows.c..-controls.resources_6595b64144ccf1df_*",
        "arm64_microsoft.windows.common-controls_6595b64144ccf1df_*",
        "arm64_microsoft.windows.gdiplus_6595b64144ccf1df_*",
        "arm64_microsoft.windows.i..utomation.proxystub_6595b64144ccf1df_*",
        "arm64_microsoft.windows.isolationautomation_6595b64144ccf1df_*",
        "amd64_microsoft.vc80.crt_1fc8b3b9a1e18e3b_*",
        "amd64_microsoft.vc90.crt_1fc8b3b9a1e18e3b_*",
        "amd64_microsoft.windows.c..-controls.resources_6595b64144ccf1df_*",
        "amd64_microsoft.windows.common-controls_6595b64144ccf1df_*",
        "amd64_microsoft.windows.gdiplus_6595b64144ccf1df_*",
        "amd64_microsoft.windows.i..utomation.proxystub_6595b64144ccf1df_*",
        "amd64_microsoft.windows.isolationautomation_6595b64144ccf1df_*",
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

        if (profile == SlimProfile.Core)
        {
            ops.AddRange(CoreApps.Select(a => new RemoveProvisionedAppx(a)));
            ops.AddRange(CorePackages.Select(p => new RemovePackage(p)));
            ops.Add(new DeleteImagePath(@"Program Files (x86)\Microsoft\Edge", TakeOwnership: true));
            ops.Add(new DeleteImagePath(@"Program Files (x86)\Microsoft\EdgeUpdate", TakeOwnership: true));
            ops.Add(new DeleteImagePath(@"Program Files (x86)\Microsoft\EdgeCore", TakeOwnership: true));
            ops.Add(new DeleteImagePath(@"Windows\System32\Microsoft-Edge-Webview", TakeOwnership: true));
            ops.Add(new DeleteImagePath(@"Windows\System32\Recovery\winre.wim", TakeOwnership: true));
            ops.Add(new DeleteImagePath(@"Windows\System32\OneDriveSetup.exe", TakeOwnership: true));
            ops.Add(new DeleteImagePath(@"Windows\SysWOW64\OneDriveSetup.exe", TakeOwnership: true));
            ops.Add(new TrimComponentStore(ComponentStoreKeep));
            ops.AddRange(LiteRegistry());
            ops.AddRange(CoreRegistry());
            ops.AddRange(CoreScheduledTasks.Select(t => new DeleteImagePath($@"Windows\System32\Tasks\Microsoft\Windows\{t}", TakeOwnership: true)));
            // After the trim most of what /ResetBase would remove is gone; a failure here leaves a working image.
            ops.Add(new ComponentCleanup(ResetBase: true, Optional: true));
        }
        else
        {
            ops.AddRange(LiteRegistry());
            ops.Add(new ComponentCleanup(ResetBase: false));
        }

        foreach (var op in ops.OfType<DeleteImagePath>())
        {
            if (IsProtectedPath(op.RelativePath))
            {
                throw new InvalidOperationException($"Plan would delete protected path {op.RelativePath}.");
            }
        }
        return ops;
    }

    internal static readonly string[] CoreScheduledTasks =
    [
        @"Application Experience\Microsoft Compatibility Appraiser",
        @"Application Experience\ProgramDataUpdater",
        @"Chkdsk\Proxy",
        "Customer Experience Improvement Program",
        @"Windows Error Reporting\QueueReporting",
    ];

    private static IEnumerable<SlimOperation> LiteRegistry()
    {
        const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        const string Policies = @"Policies\Microsoft\Windows";

        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0);
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1);
        yield return Dword(RegistryHive.Software, $@"{Policies}\Windows Chat", "ChatIcon", 3);
        yield return Dword(RegistryHive.Software, @"Microsoft\Windows\CurrentVersion\OOBE", "BypassNRO", 1);
        yield return Text(RegistryHive.Software, @"Microsoft\PolicyManager\current\device\Start", "ConfigureStartPins", "{\"pinnedList\": [{}]}");
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

    /// <summary>tiny11 Core's registry changes that Lite does not already make.</summary>
    private static IEnumerable<SlimOperation> CoreRegistry()
    {
        const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        const string Wu = @"Policies\Microsoft\Windows\WindowsUpdate";

        foreach (var hive in new[] { RegistryHive.SystemDefaultUser, RegistryHive.DefaultUser })
        {
            yield return Dword(hive, @"Control Panel\UnsupportedHardwareNotificationCache", "SV1", 0);
            yield return Dword(hive, @"Control Panel\UnsupportedHardwareNotificationCache", "SV2", 0);
        }
        foreach (var check in new[] { "BypassCPUCheck", "BypassRAMCheck", "BypassSecureBootCheck", "BypassStorageCheck", "BypassTPMCheck" })
        {
            yield return Dword(RegistryHive.System, @"Setup\LabConfig", check, 1);
        }
        yield return Dword(RegistryHive.System, @"Setup\MoSetup", "AllowUpgradesWithUnsupportedTPMOrCPU", 1);

        foreach (var id in new[] { "310093", "338388", "338389", "338393", "353694", "353696" })
        {
            yield return Dword(RegistryHive.DefaultUser, Cdm, $"SubscribedContent-{id}Enabled", 0);
        }
        yield return new DeleteRegistryKey(RegistryHive.DefaultUser, $@"{Cdm}\Subscriptions");
        yield return new DeleteRegistryKey(RegistryHive.DefaultUser, $@"{Cdm}\SuggestedApps");
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\Windows\CloudContent", "DisableConsumerAccountStateContent", 1);
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\Windows\CloudContent", "DisableCloudOptimizedContent", 1);
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\PushToInstall", "DisablePushToInstall", 1);
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\MRT", "DontOfferThroughWUAU", 1);
        yield return Dword(RegistryHive.Software, @"Microsoft\Windows\CurrentVersion\ReserveManager", "ShippedWithReserves", 0);
        yield return Dword(RegistryHive.System, @"ControlSet001\Control\BitLocker", "PreventDeviceEncryption", 1);
        yield return new DeleteRegistryKey(RegistryHive.Software, @"WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge");
        yield return new DeleteRegistryKey(RegistryHive.Software, @"WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge Update");
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\Windows\OneDrive", "DisableFileSyncNGSC", 1);

        yield return Dword(RegistryHive.DefaultUser, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0);
        yield return Dword(RegistryHive.DefaultUser, @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0);
        yield return Dword(RegistryHive.DefaultUser, @"Software\Microsoft\Input\TIPC", "Enabled", 0);
        yield return Dword(RegistryHive.DefaultUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1);
        yield return Dword(RegistryHive.DefaultUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1);
        yield return Dword(RegistryHive.DefaultUser, @"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0);
        yield return Dword(RegistryHive.DefaultUser, @"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0);
        yield return Service("dmwappushservice");

        yield return Dword(RegistryHive.Software, @"Microsoft\Windows\CurrentVersion\WindowsUpdate\Orchestrator\UScheduler\OutlookUpdate", "workCompleted", 1);
        yield return Dword(RegistryHive.Software, @"Microsoft\Windows\CurrentVersion\WindowsUpdate\Orchestrator\UScheduler\DevHomeUpdate", "workCompleted", 1);
        yield return new DeleteRegistryKey(RegistryHive.Software, @"Microsoft\WindowsUpdate\Orchestrator\UScheduler_Oobe\OutlookUpdate");
        yield return new DeleteRegistryKey(RegistryHive.Software, @"Microsoft\WindowsUpdate\Orchestrator\UScheduler_Oobe\DevHomeUpdate");
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1);
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\Edge", "HubsSidebarEnabled", 0);
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1);
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\Teams", "DisableInstallation", 1);
        yield return Dword(RegistryHive.Software, @"Policies\Microsoft\Windows\Windows Mail", "PreventRun", 1);

        // Windows Update: stop it after OOBE, point it nowhere and remove its repair services.
        const string RunOnce = @"Microsoft\Windows\CurrentVersion\RunOnce";
        yield return Text(RegistryHive.Software, RunOnce, "StopWUPostOOBE1", "net stop wuauserv");
        yield return Text(RegistryHive.Software, RunOnce, "StopWUPostOOBE2", "sc stop wuauserv");
        yield return Text(RegistryHive.Software, RunOnce, "StopWUPostOOBE3", "sc config wuauserv start= disabled");
        yield return Text(RegistryHive.Software, RunOnce, "DisableWUPostOOBE1", @"reg add HKLM\SYSTEM\CurrentControlSet\Services\wuauserv /v Start /t REG_DWORD /d 4 /f");
        yield return Text(RegistryHive.Software, RunOnce, "DisableWUPostOOBE2", @"reg add HKLM\SYSTEM\ControlSet001\Services\wuauserv /v Start /t REG_DWORD /d 4 /f");
        yield return Dword(RegistryHive.Software, Wu, "DoNotConnectToWindowsUpdateInternetLocations", 1);
        yield return Dword(RegistryHive.Software, Wu, "DisableWindowsUpdateAccess", 1);
        yield return Text(RegistryHive.Software, Wu, "WUServer", "localhost");
        yield return Text(RegistryHive.Software, Wu, "WUStatusServer", "localhost");
        yield return Text(RegistryHive.Software, Wu, "UpdateServiceUrlAlternate", "localhost");
        yield return Dword(RegistryHive.Software, $@"{Wu}\AU", "UseWUServer", 1);
        yield return Dword(RegistryHive.Software, $@"{Wu}\AU", "NoAutoUpdate", 1);
        yield return Dword(RegistryHive.Software, @"Microsoft\Windows\CurrentVersion\OOBE", "DisableOnline", 1);
        yield return Service("wuauserv");
        yield return new DeleteRegistryKey(RegistryHive.System, @"ControlSet001\Services\WaaSMedicSVC");
        yield return new DeleteRegistryKey(RegistryHive.System, @"ControlSet001\Services\UsoSvc");

        foreach (var svc in new[] { "WinDefend", "WdNisSvc", "WdNisDrv", "WdFilter", "Sense" })
        {
            yield return Service(svc);
        }
        yield return Text(RegistryHive.Software, @"Microsoft\Windows\CurrentVersion\Policies\Explorer", "SettingsPageVisibility", "hide:virus;windowsupdate");
    }

    /// <summary>Disable a service that exists; never create a bare service key.</summary>
    private static SetRegistryValue Service(string name) =>
        new(RegistryHive.System, $@"ControlSet001\Services\{name}", "Start", RegistryValueKind.DWord, "4", OnlyIfKeyExists: true);

    private static SetRegistryValue Text(RegistryHive hive, string key, string name, string value) =>
        new(hive, key, name, RegistryValueKind.String, value);

    private static SetRegistryValue Dword(RegistryHive hive, string key, string name, uint value) =>
        new(hive, key, name, RegistryValueKind.DWord, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static string Summary(SlimProfile profile) => profile switch
    {
        SlimProfile.None => "Stock Windows with the phone drivers added.",
        SlimProfile.Lite => "Removes consumer inbox apps, ads and telemetry defaults, and allows a local account in OOBE. Windows Update keeps working.",
        SlimProfile.Core => "tiny11 Core: Lite plus removal of Edge, OneDrive, Defender, WinRE, Windows Update and most of the component store. Smallest and fastest, but it can no longer be updated or repaired and has no antivirus.",
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };
}
