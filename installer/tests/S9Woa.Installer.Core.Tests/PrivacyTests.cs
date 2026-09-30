// SPDX-License-Identifier: BSD-2-Clause-Patent
using S9Woa.Installer.Core.Device;
using S9Woa.Installer.Core.Processes;
using S9Woa.Installer.Core.Stages;

namespace S9Woa.Installer.Core.Tests;

public class PrivacyTests
{
    private const string Serial = "a1b2c3d4e5f60718";

    [Fact]
    public void SerialsBecomeAStableAliasAndTheUserFolderIsHidden()
    {
        var profile = Path.Combine(Path.GetTempPath(), "Users", "someone");
        var redactor = new Redactor(profile + Path.DirectorySeparatorChar);
        redactor.AddSerial(Serial);

        var text = redactor.Redact(
            $@"adb: device '{Serial.ToUpperInvariant()}' not found; backups in {profile.ToUpperInvariant()}\AppData\Local\S9WoaInstaller\backups\{Serial}");

        Assert.DoesNotContain(Serial, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("someone", text, StringComparison.OrdinalIgnoreCase);
        var alias = Redactor.Alias(Serial);
        Assert.Matches("^phone-[0-9a-f]{8}$", alias);
        Assert.Equal(alias, Redactor.Alias(Serial.ToUpperInvariant()));
        Assert.Equal($@"adb: device '{alias}' not found; backups in %USERPROFILE%\AppData\Local\S9WoaInstaller\backups\{alias}", text);
    }

    [Fact]
    public void ThePcsOwnWindowsVersionIsHiddenButTheMediaVersionIsNot()
    {
        var redactor = new Redactor(null);
        redactor.Hide("10.0.26999.1234");
        redactor.Hide("26999.1.arm64fre.example_branch.260101-0000");

        var dism = "Deployment Image Servicing and Management tool\r\nVersion: 10.0.26100.28089\r\n\r\n"
            + "Image Version: 10.0.22621.2428\r\n\r\nError: 87";
        var text = redactor.Redact(dism + "\nhost 10.0.26999.1234 (26999.1.arm64fre.example_branch.260101-0000)");

        Assert.DoesNotContain("26100.28089", text, StringComparison.Ordinal);
        Assert.DoesNotContain("26999", text, StringComparison.Ordinal);
        Assert.Contains("Version: (hidden)", text, StringComparison.Ordinal);
        Assert.Contains("Image Version: 10.0.22621.2428", text, StringComparison.Ordinal);
        Assert.Contains("Removing package ~10.0.22621.2428", redactor.Redact("Removing package ~10.0.22621.2428"), StringComparison.Ordinal);
    }

    [Fact]
    public void ShortOrEmptyValuesAreNotTreatedAsSerials()
    {
        var redactor = new Redactor(null);
        redactor.AddSerial("S9");
        redactor.AddSerial("  ");
        redactor.AddSerial(null);
        Assert.Equal("Galaxy S9+ in TWRP", redactor.Redact("Galaxy S9+ in TWRP"));
        Assert.Equal("", redactor.Redact(null));
    }

    [Fact]
    public async Task AdbReportsEveryListedSerial()
    {
        var seen = new List<string>();
        var adb = new AdbClient("adb.exe", new ScriptedRunner($"List of devices attached\n{Serial}\trecovery product:star2ltexx model:SM_G965F device:star2lte\n"), seen.Add);

        var devices = await adb.ListDevicesAsync();

        Assert.Single(devices);
        Assert.Equal([Serial], seen);
    }

    [Fact]
    public void ADataFolderNextToTheAppMakesItPortable()
    {
        var app = Directory.CreateTempSubdirectory("s9woa-app-").FullName;
        var fallback = Path.Combine(app, "default-data");
        try
        {
            Assert.Equal(fallback, InstallState.ResolveDirectory(app, fallback));
            var data = Directory.CreateDirectory(Path.Combine(app, InstallState.PortableFolderName)).FullName;
            Assert.Equal(data, InstallState.ResolveDirectory(app, fallback));
        }
        finally
        {
            Directory.Delete(app, recursive: true);
        }
    }

    private sealed class ScriptedRunner(string stdout) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult(new ProcessResult(0, stdout, ""));
    }
}
