// SPDX-License-Identifier: BSD-2-Clause-Patent
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using S9Woa.Installer.Core;

namespace S9Woa.Installer.App;

/// <summary>Display wrapper for a <see cref="CheckResult"/>.</summary>
[Microsoft.UI.Xaml.Data.Bindable]
public sealed partial class CheckItem(CheckResult result)
{
    public string Title => result.Title;
    public string Message => AppServices.Redact(result.Detail);
    public string Remedy => AppServices.Redact(result.Action);
    public Visibility RemedyVisibility => string.IsNullOrEmpty(result.Action) ? Visibility.Collapsed : Visibility.Visible;

    public string Glyph => result.Severity switch
    {
        CheckSeverity.Pass => "\uE73E",
        CheckSeverity.Info => "\uE946",
        CheckSeverity.Warning => "\uE7BA",
        _ => "\uEA39",
    };

    public Brush Brush => (Brush)Application.Current.Resources[result.Severity switch
    {
        CheckSeverity.Pass => "SystemFillColorSuccessBrush",
        CheckSeverity.Info => "SystemFillColorAttentionBrush",
        CheckSeverity.Warning => "SystemFillColorCautionBrush",
        _ => "SystemFillColorCriticalBrush",
    }];
}
