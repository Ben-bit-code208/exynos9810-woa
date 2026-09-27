// SPDX-License-Identifier: BSD-2-Clause-Patent
namespace S9Woa.Installer.Core;

public enum CheckSeverity
{
    Pass,
    Info,
    Warning,
    Blocker,
}

public sealed record CheckResult(string Id, string Title, CheckSeverity Severity, string Detail, string? Action = null);

public static class CheckResultExtensions
{
    public static bool HasBlockers(this IEnumerable<CheckResult> results) =>
        results.Any(r => r.Severity == CheckSeverity.Blocker);
}
