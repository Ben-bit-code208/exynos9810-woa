// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace S9Woa.Installer.Core.Toolset;

/// <summary>Read-only HKLM access, abstracted for tests.</summary>
public interface IRegistryReader
{
    bool KeyExists(string hklmPath);
    IReadOnlyList<string> SubKeyNames(string hklmPath);
    string? GetString(string hklmPath, string valueName);
}

public sealed class LocalMachineRegistry : IRegistryReader
{
    public bool KeyExists(string hklmPath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(hklmPath);
        return key is not null;
    }

    public IReadOnlyList<string> SubKeyNames(string hklmPath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(hklmPath);
        return key?.GetSubKeyNames() ?? [];
    }

    public string? GetString(string hklmPath, string valueName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(hklmPath);
        return key?.GetValue(valueName) as string;
    }
}

/// <summary>Outcome of an Authenticode check.</summary>
public sealed record SignatureInfo(bool Trusted, string? Subject);

public interface ISignatureVerifier
{
    SignatureInfo Verify(string file);
}

/// <summary>
/// Verifies an Authenticode signature with <c>WinVerifyTrust</c> (chain to a
/// trusted root, no UI) and reports the signer subject.
/// </summary>
public sealed class AuthenticodeVerifier : ISignatureVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public SignatureInfo Verify(string file)
    {
        var trusted = WinVerifyTrustFile(file) == 0;
        string? subject = null;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
            subject = cert.Subject;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            trusted = false;
        }
        return new SignatureInfo(trusted, subject);
    }

    private static int WinVerifyTrustFile(string file)
    {
        var fileInfo = new WintrustFileInfo
        {
            CbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            FilePath = file,
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new WintrustData
            {
                CbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                UiChoice = 2,          // WTD_UI_NONE
                RevocationChecks = 0,  // WTD_REVOKE_NONE
                UnionChoice = 1,       // WTD_CHOICE_FILE
                File = fileInfoPtr,
                StateAction = 1,       // WTD_STATEACTION_VERIFY
            };
            var result = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
            data.StateAction = 2;      // WTD_STATEACTION_CLOSE
            _ = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
            return result;
        }
        finally
        {
            Marshal.DestroyStructure<WintrustFileInfo>(fileInfoPtr);
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint CbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint CbStruct;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, ref WintrustData data);
}

/// <summary>Where the toolset looks for and stores things.</summary>
public sealed record ToolsetPaths(
    string AppDirectory,
    string DataDirectory,
    string LocalAppData,
    string ProgramFiles,
    string? PathVariable)
{
    public string ToolsetDirectory => Path.Combine(DataDirectory, "toolset");
    public string PayloadDirectory => Path.Combine(ToolsetDirectory, "payload");
    public string BundledPayloadDirectory => Path.Combine(AppDirectory, "payload");

    public static ToolsetPaths ForCurrentUser(string appDirectory, string dataDirectory) => new(
        appDirectory,
        dataDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetEnvironmentVariable("PATH"));
}

/// <summary>
/// Finds program tools: an explicit override, the app's <c>tools\</c> folder,
/// winget portable installs (per-user and machine, packages and links), then PATH.
/// winget's links folder is added to PATH only for new processes, so it is
/// searched directly to find tools installed during this session.
/// </summary>
public static class ToolLocator
{
    public static string? Find(ToolsetPaths paths, string fileName, string? wingetId, string? overridePath = null,
        params string[] appRelativeFolders)
    {
        if (overridePath is not null && File.Exists(overridePath))
        {
            return overridePath;
        }
        foreach (var folder in appRelativeFolders)
        {
            var dir = Path.Combine(paths.AppDirectory, folder);
            var candidate = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, fileName).FirstOrDefault() : null;
            if (candidate is not null)
            {
                return candidate;
            }
        }
        if (wingetId is not null)
        {
            foreach (var root in new[]
                     {
                         Path.Combine(paths.LocalAppData, "Microsoft", "WinGet"),
                         Path.Combine(paths.ProgramFiles, "WinGet"),
                     })
            {
                var packages = Path.Combine(root, "Packages");
                if (Directory.Exists(packages))
                {
                    foreach (var dir in Directory.EnumerateDirectories(packages, wingetId + "_*"))
                    {
                        var hit = Directory.EnumerateFiles(dir, fileName, SearchOption.AllDirectories)
                            .OrderBy(p => p.Length).FirstOrDefault();
                        if (hit is not null)
                        {
                            return hit;
                        }
                    }
                }
                var links = Path.Combine(root, "Links");
                if (Directory.Exists(links))
                {
                    var link = Directory.EnumerateFiles(links, fileName).FirstOrDefault();
                    if (link is not null)
                    {
                        return link;
                    }
                }
            }
        }
        if (!fileName.Contains('*', StringComparison.Ordinal))
        {
            foreach (var dir in (paths.PathVariable ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }
}

/// <summary>Installs winget packages. Success is judged by re-detection, not exit codes.</summary>
public sealed class WingetClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);
    private readonly Processes.IProcessRunner _runner;

    public WingetClient(Processes.IProcessRunner runner, string? wingetPath)
    {
        _runner = runner;
        WingetPath = wingetPath;
    }

    public string? WingetPath { get; }

    public static string? Locate(ToolsetPaths paths)
    {
        var alias = Path.Combine(paths.LocalAppData, "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(alias) ? alias : ToolLocator.Find(paths, "winget.exe", null);
    }

    internal static IReadOnlyList<string> InstallArguments(string packageId) =>
    [
        "install", "--id", packageId, "--exact", "--source", "winget",
        "--silent", "--disable-interactivity",
        "--accept-package-agreements", "--accept-source-agreements",
    ];

    public async Task<Processes.ProcessResult> InstallAsync(string packageId, CancellationToken ct = default)
    {
        if (WingetPath is null)
        {
            throw new InvalidOperationException("winget is not available on this PC. Install \"App Installer\" from the Microsoft Store, or choose the file manually.");
        }
        try
        {
            return await _runner.RunAsync(WingetPath, InstallArguments(packageId), Timeout, ct).ConfigureAwait(false);
        }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException($"Could not start winget: {e.Message}");
        }
    }
}
