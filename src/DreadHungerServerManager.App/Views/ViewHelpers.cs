using DreadHungerServerManager.App.ViewModels;
using DreadHungerServerManager.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Globalization.NumberFormatting;

namespace DreadHungerServerManager.App.Views;

/// <summary>x:Bindの関数バインドで使う変換。</summary>
public static class ViewHelpers
{
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static Brush StateBrush(ServerState state) => state switch
    {
        ServerState.Running => Brush("SystemFillColorSuccessBrush"),
        ServerState.Starting or ServerState.Stopping => Brush("SystemFillColorCautionBrush"),
        _ => Brush("SystemFillColorNeutralBrush"),
    };

    public static Brush CloudStateBrush(CloudInstanceState state) => state switch
    {
        CloudInstanceState.Running => Brush("SystemFillColorSuccessBrush"),
        CloudInstanceState.Starting or CloudInstanceState.Stopping => Brush("SystemFillColorCautionBrush"),
        CloudInstanceState.Error => Brush("SystemFillColorCriticalBrush"),
        _ => Brush("SystemFillColorNeutralBrush"),
    };

    public static Brush LogBrush(LogLineKind kind) => kind switch
    {
        LogLineKind.Error => Brush("SystemFillColorCriticalBrush"),
        LogLineKind.Warning => Brush("SystemFillColorCautionBrush"),
        LogLineKind.Success => Brush("SystemFillColorSuccessBrush"),
        LogLineKind.Manager => Brush("AccentTextFillColorPrimaryBrush"),
        _ => Brush("TextFillColorPrimaryBrush"),
    };

    public static string CheckGlyph(bool done) => done ? "" : "";

    public static Brush CheckBrush(bool done) => done ? Brush("SystemFillColorSuccessBrush") : Brush("TextFillColorTertiaryBrush");

    public static string ExecutableGlyph(bool exists) => exists ? "" : "";

    public static Brush ExecutableBrush(bool exists) => exists ? Brush("SystemFillColorSuccessBrush") : Brush("SystemFillColorCautionBrush");

    public static InfoBarSeverity ToSeverity(NoticeSeverity severity) => severity switch
    {
        NoticeSeverity.Success => InfoBarSeverity.Success,
        NoticeSeverity.Warning => InfoBarSeverity.Warning,
        NoticeSeverity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    public static Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>倍率は「1.00」のように小数2桁で表示する。</summary>
    public static INumberFormatter2 CreateMultiplierFormatter() => new DecimalFormatter
    {
        IntegerDigits = 1,
        FractionDigits = 2,
        IsGrouped = false,
        NumberRounder = new IncrementNumberRounder { Increment = 0.01, RoundingAlgorithm = RoundingAlgorithm.RoundHalfUp },
    };
}
