// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using S9Woa.Installer.Core.Toolset;

namespace S9Woa.Installer.App;

/// <summary>One tool on the Setup page: its status and which actions apply.</summary>
[Microsoft.UI.Xaml.Data.Bindable]
public sealed partial class ToolRow(ToolDefinition definition) : INotifyPropertyChanged
{
    private ToolStatus _status = ToolStatus.Missing("");
    private bool _busy;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ToolDefinition Definition => definition;
    public string Id => definition.Id;
    public string Name => definition.Name;
    public string Purpose => definition.Purpose;
    public string Badge => definition.Required ? "" : "Optional";
    public Visibility BadgeVisibility => definition.Required ? Visibility.Collapsed : Visibility.Visible;

    public ToolStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            Changed(nameof(Status), nameof(Detail), nameof(Glyph), nameof(Brush), nameof(WingetVisibility),
                nameof(OpenPageVisibility), nameof(ReleaseVisibility), nameof(PickFileLabel));
        }
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            _busy = value;
            Changed(nameof(Busy), nameof(ActionsEnabled));
        }
    }

    public bool ActionsEnabled => !_busy;
    public string Detail => AppServices.Redact(_status.Detail);

    public string Glyph => _status.State switch
    {
        ToolState.Ready => "\uE73E",
        ToolState.Deferred => "\uE823",
        ToolState.Error => "\uEA39",
        _ => "\uE7BA",
    };

    public Brush Brush => (Brush)Application.Current.Resources[_status.State switch
    {
        ToolState.Ready => "SystemFillColorSuccessBrush",
        ToolState.Deferred => "SystemFillColorAttentionBrush",
        ToolState.Error => "SystemFillColorCriticalBrush",
        _ => "SystemFillColorCautionBrush",
    }];

    // Acquisition actions are offered until the tool is ready; replacing it stays available.
    public Visibility WingetVisibility => ShowUntilReady(ToolSource.Winget);
    public Visibility PickFileVisibility => Show(ToolSource.PickFile);
    public Visibility OpenPageVisibility => ShowUntilReady(ToolSource.OpenPage);
    public Visibility ReleaseVisibility => ShowUntilReady(ToolSource.Release);
    public Visibility BuildFolderVisibility => Show(ToolSource.BuildFolder);
    public Visibility LaunchVisibility => Show(ToolSource.Launch);

    public string PickFileLabel => (definition.Kind, _status.State == ToolState.Ready) switch
    {
        (ToolKind.Driver, _) => "Run downloaded installer…",
        (ToolKind.Payload, false) => "Choose downloaded file…",
        (_, false) => "Choose file…",
        _ => "Use a different file…",
    };

    private Visibility Show(ToolSource source) => definition.Sources.HasFlag(source) ? Visibility.Visible : Visibility.Collapsed;

    private Visibility ShowUntilReady(ToolSource source) =>
        _status.State == ToolState.Ready ? Visibility.Collapsed : Show(source);

    private void Changed(params string[] names)
    {
        foreach (var n in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }
}
