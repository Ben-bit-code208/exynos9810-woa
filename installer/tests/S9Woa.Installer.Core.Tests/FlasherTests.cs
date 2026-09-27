// SPDX-License-Identifier: BSD-2-Clause-Patent
using S9Woa.Installer.Core.Deploy;
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Tests;

public class FlasherTests
{
    private sealed class ScriptedRunner : IProcessRunner
    {
        public required Func<string, IReadOnlyList<string>, ProcessResult> Handler { get; init; }
        public List<string> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"{Path.GetFileName(fileName)} {string.Join(' ', arguments)}");
            return Task.FromResult(Handler(fileName, arguments));
        }
    }

    private sealed class FakeFlasher : ITwrpFlasher
    {
        public required string Name { get; init; }
        public bool Available { get; init; }
        public bool Flashed { get; private set; }
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(Available);
        public Task FlashRecoveryAsync(string twrpImage, IProgress<string>? log = null, CancellationToken ct = default)
        {
            Flashed = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task HeimdallDetectsAndFlashesByName()
    {
        var img = Path.GetTempFileName();
        try
        {
            var runner = new ScriptedRunner
            {
                Handler = (_, args) => new ProcessResult(0, "", ""),
            };
            var heimdall = Path.GetTempFileName(); // stand-in for heimdall.exe existing
            var flasher = new HeimdallTwrpFlasher(heimdall, runner);
            Assert.True(await flasher.IsAvailableAsync());
            await flasher.FlashRecoveryAsync(img);
            Assert.Contains(runner.Calls, c => c.Contains("flash --RECOVERY", StringComparison.Ordinal) && c.Contains("--no-reboot", StringComparison.Ordinal));
            File.Delete(heimdall);
        }
        finally
        {
            File.Delete(img);
        }
    }

    [Fact]
    public async Task HeimdallUnavailableWhenExeMissing()
    {
        var flasher = new HeimdallTwrpFlasher(@"C:\does\not\exist\heimdall.exe", new ScriptedRunner { Handler = (_, _) => new ProcessResult(0, "", "") });
        Assert.False(await flasher.IsAvailableAsync());
    }

    [Fact]
    public async Task ServicePrefersFirstAvailableFlasher()
    {
        var native = new FakeFlasher { Name = "Native", Available = false };
        var heimdall = new FakeFlasher { Name = "Heimdall", Available = true };
        var svc = new TwrpFlashService([native, heimdall]);

        var resolved = await svc.ResolveAsync();
        Assert.Same(heimdall, resolved);

        await svc.FlashRecoveryAsync(@"C:\twrp.img");
        Assert.True(heimdall.Flashed);
        Assert.False(native.Flashed);
    }

    [Fact]
    public async Task ServiceThrowsWhenNoFlasherAvailable()
    {
        var svc = new TwrpFlashService([new FakeFlasher { Name = "Native", Available = false }]);
        Assert.Null(await svc.ResolveAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.FlashRecoveryAsync(@"C:\twrp.img"));
    }
}
