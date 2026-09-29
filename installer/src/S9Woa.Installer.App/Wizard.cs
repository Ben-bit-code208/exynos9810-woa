// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace S9Woa.Installer.App;

/// <summary>
/// Implemented by each wizard page. The shell owns navigation: it asks the page whether it
/// can move on, which label the primary button should carry, and gives it a chance to act
/// (save, start work) before leaving.
/// </summary>
internal interface IWizardStep
{
    /// <summary>True when the primary button should be enabled.</summary>
    bool CanAdvance { get; }

    /// <summary>False while the page is busy and must not be left.</summary>
    bool CanGoBack => true;

    /// <summary>Label for the primary button; null means "Continue".</summary>
    string? NextLabel => null;

    /// <summary>Raised when <see cref="CanAdvance"/>, <see cref="CanGoBack"/> or <see cref="NextLabel"/> changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>Runs when the primary button is pressed. Return false to stay on the page.</summary>
    Task<bool> OnAdvanceAsync() => Task.FromResult(true);
}

public enum StepState
{
    Upcoming,
    Current,
    Done,
}

/// <summary>One entry in the step rail.</summary>
[Microsoft.UI.Xaml.Data.Bindable]
public sealed partial class WizardStepItem(int index, string tag, string title, Type page) : INotifyPropertyChanged
{
    private StepState _state;
    private bool _reachable;
    private bool _isLast;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index => index;
    public string Tag => tag;
    public string Title => title;
    public Type Page => page;
    public string Number => (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public StepState State
    {
        get => _state;
        set { _state = value; Changed(); }
    }

    /// <summary>The user may jump to this step from the rail (it is at or before the furthest step reached).</summary>
    public bool Reachable
    {
        get => _reachable;
        set { _reachable = value; Changed(); }
    }

    public bool IsLast
    {
        get => _isLast;
        set { _isLast = value; Changed(); }
    }

    public Visibility NumberVisibility => _state == StepState.Done ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CheckVisibility => _state == StepState.Done ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConnectorVisibility => _isLast ? Visibility.Collapsed : Visibility.Visible;

    public Brush CircleFill => Res(_state switch
    {
        StepState.Current => "AccentFillColorDefaultBrush",
        StepState.Done => "AccentFillColorDefaultBrush",
        _ => "ControlFillColorTransparentBrush",
    });

    public Brush CircleStroke => Res(_state == StepState.Upcoming ? "ControlStrongStrokeColorDefaultBrush" : "AccentFillColorDefaultBrush");

    public Brush CircleForeground => Res(_state == StepState.Upcoming ? "TextFillColorSecondaryBrush" : "TextOnAccentFillColorPrimaryBrush");

    public Brush TitleForeground => Res(_state switch
    {
        StepState.Current => "TextFillColorPrimaryBrush",
        StepState.Done => "TextFillColorSecondaryBrush",
        _ => "TextFillColorTertiaryBrush",
    });

    public FontWeight TitleWeight => _state == StepState.Current ? FontWeights.SemiBold : FontWeights.Normal;

    public Brush ConnectorBrush => Res(_state == StepState.Done ? "AccentFillColorDefaultBrush" : "DividerStrokeColorDefaultBrush");

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private void Changed()
    {
        foreach (var n in new[]
                 {
                     nameof(State), nameof(Reachable), nameof(IsLast), nameof(NumberVisibility), nameof(CheckVisibility),
                     nameof(ConnectorVisibility), nameof(CircleFill), nameof(CircleStroke), nameof(CircleForeground),
                     nameof(TitleForeground), nameof(TitleWeight), nameof(ConnectorBrush),
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }
}
