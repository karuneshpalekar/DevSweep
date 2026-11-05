using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace DevSweep.App.Toolkit;

/// <summary>
/// DevSweep's styles. Every colour is a Fluent theme resource (ThemeResource), so light,
/// dark and high contrast all come from Windows; the dictionary is parsed once at start-up
/// (there is no XAML compiler step), and the smoke test proves it parses.
/// </summary>
internal static class Theme
{
    private const string Xaml = """
        <ResourceDictionary
            xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <ResourceDictionary.ThemeDictionaries>
            <ResourceDictionary x:Key="Light">
              <Thickness x:Key="DsBadgeBorderThickness">0</Thickness>
            </ResourceDictionary>
            <ResourceDictionary x:Key="Dark">
              <Thickness x:Key="DsBadgeBorderThickness">0</Thickness>
            </ResourceDictionary>
            <ResourceDictionary x:Key="HighContrast">
              <Thickness x:Key="DsBadgeBorderThickness">1</Thickness>
            </ResourceDictionary>
          </ResourceDictionary.ThemeDictionaries>

          <Style x:Key="DsRoot" TargetType="Grid">
            <Setter Property="Background" Value="{ThemeResource SolidBackgroundFillColorBaseBrush}" />
          </Style>
          <Style x:Key="DsTitle" TargetType="TextBlock">
            <Setter Property="FontSize" Value="28" />
            <Setter Property="FontWeight" Value="SemiBold" />
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorPrimaryBrush}" />
            <Setter Property="TextWrapping" Value="Wrap" />
          </Style>
          <Style x:Key="DsSubtitle" TargetType="TextBlock">
            <Setter Property="FontSize" Value="20" />
            <Setter Property="FontWeight" Value="SemiBold" />
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorPrimaryBrush}" />
            <Setter Property="TextWrapping" Value="Wrap" />
          </Style>
          <Style x:Key="DsBig" TargetType="TextBlock">
            <Setter Property="FontSize" Value="34" />
            <Setter Property="FontWeight" Value="SemiBold" />
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorPrimaryBrush}" />
          </Style>
          <Style x:Key="DsBigAccent" TargetType="TextBlock">
            <Setter Property="FontSize" Value="34" />
            <Setter Property="FontWeight" Value="SemiBold" />
            <Setter Property="Foreground" Value="{ThemeResource AccentTextFillColorPrimaryBrush}" />
          </Style>
          <Style x:Key="DsStrong" TargetType="TextBlock">
            <Setter Property="FontSize" Value="14" />
            <Setter Property="FontWeight" Value="SemiBold" />
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorPrimaryBrush}" />
            <Setter Property="TextWrapping" Value="Wrap" />
          </Style>
          <Style x:Key="DsBody" TargetType="TextBlock">
            <Setter Property="FontSize" Value="14" />
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorPrimaryBrush}" />
            <Setter Property="TextWrapping" Value="Wrap" />
          </Style>
          <Style x:Key="DsSecondary" TargetType="TextBlock">
            <Setter Property="FontSize" Value="14" />
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}" />
            <Setter Property="TextWrapping" Value="Wrap" />
          </Style>
          <Style x:Key="DsCaption" TargetType="TextBlock">
            <Setter Property="FontSize" Value="12" />
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}" />
            <Setter Property="TextWrapping" Value="Wrap" />
          </Style>
          <Style x:Key="DsSectionHeading" TargetType="TextBlock">
            <Setter Property="FontSize" Value="12" />
            <Setter Property="FontWeight" Value="SemiBold" />
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}" />
          </Style>
          <Style x:Key="DsAccentText" TargetType="TextBlock">
            <Setter Property="FontSize" Value="12" />
            <Setter Property="FontWeight" Value="SemiBold" />
            <Setter Property="Foreground" Value="{ThemeResource AccentTextFillColorPrimaryBrush}" />
          </Style>
          <Style x:Key="DsWarningText" TargetType="TextBlock">
            <Setter Property="FontSize" Value="14" />
            <Setter Property="Foreground" Value="{ThemeResource SystemFillColorCautionBrush}" />
            <Setter Property="TextWrapping" Value="Wrap" />
          </Style>
          <Style x:Key="DsCriticalText" TargetType="TextBlock">
            <Setter Property="FontSize" Value="14" />
            <Setter Property="Foreground" Value="{ThemeResource SystemFillColorCriticalBrush}" />
            <Setter Property="TextWrapping" Value="Wrap" />
          </Style>
          <Style x:Key="DsMono" TargetType="TextBlock">
            <Setter Property="FontSize" Value="12" />
            <Setter Property="FontFamily" Value="Cascadia Mono, Consolas" />
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorPrimaryBrush}" />
            <Setter Property="TextWrapping" Value="Wrap" />
            <Setter Property="IsTextSelectionEnabled" Value="True" />
          </Style>
          <Style x:Key="DsCard" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource CardBackgroundFillColorDefaultBrush}" />
            <Setter Property="BorderBrush" Value="{ThemeResource CardStrokeColorDefaultBrush}" />
            <Setter Property="BorderThickness" Value="1" />
            <Setter Property="CornerRadius" Value="8" />
            <Setter Property="Padding" Value="16" />
          </Style>
          <Style x:Key="DsCodeBox" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource SubtleFillColorSecondaryBrush}" />
            <Setter Property="BorderBrush" Value="{ThemeResource CardStrokeColorDefaultBrush}" />
            <Setter Property="BorderThickness" Value="1" />
            <Setter Property="CornerRadius" Value="6" />
            <Setter Property="Padding" Value="10" />
          </Style>
          <Style x:Key="DsSelectedRow" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource SubtleFillColorSecondaryBrush}" />
            <Setter Property="BorderBrush" Value="{ThemeResource AccentFillColorDefaultBrush}" />
            <Setter Property="BorderThickness" Value="1" />
            <Setter Property="CornerRadius" Value="6" />
            <Setter Property="Padding" Value="8,4" />
          </Style>
          <Style x:Key="DsRow" TargetType="Border">
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="BorderBrush" Value="Transparent" />
            <Setter Property="BorderThickness" Value="1" />
            <Setter Property="CornerRadius" Value="6" />
            <Setter Property="Padding" Value="8,4" />
          </Style>
          <Style x:Key="DsDivider" TargetType="Border">
            <Setter Property="Height" Value="1" />
            <Setter Property="Background" Value="{ThemeResource DividerStrokeColorDefaultBrush}" />
          </Style>
          <Style x:Key="DsInspector" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource LayerFillColorDefaultBrush}" />
            <Setter Property="BorderBrush" Value="{ThemeResource CardStrokeColorDefaultBrush}" />
            <Setter Property="BorderThickness" Value="1,0,0,0" />
          </Style>
          <Style x:Key="DsScrim" TargetType="Grid">
            <Setter Property="Background" Value="{ThemeResource SmokeFillColorDefaultBrush}" />
          </Style>
          <Style x:Key="DsSheet" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource SolidBackgroundFillColorBaseBrush}" />
            <Setter Property="BorderBrush" Value="{ThemeResource SurfaceStrokeColorDefaultBrush}" />
            <Setter Property="BorderThickness" Value="1" />
            <Setter Property="CornerRadius" Value="8" />
          </Style>
          <Style x:Key="DsBarTrack" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource ControlStrongFillColorDisabledBrush}" />
            <Setter Property="CornerRadius" Value="6" />
          </Style>
          <Style x:Key="DsBarUsed" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource ControlStrongFillColorDefaultBrush}" />
          </Style>
          <Style x:Key="DsBarCleanable" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource AccentFillColorDefaultBrush}" />
          </Style>
          <Style x:Key="DsBetter" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource SystemFillColorAttentionBackgroundBrush}" />
            <Setter Property="BorderBrush" Value="{ThemeResource AccentFillColorDefaultBrush}" />
            <Setter Property="BorderThickness" Value="{ThemeResource DsBadgeBorderThickness}" />
            <Setter Property="CornerRadius" Value="8" />
            <Setter Property="Padding" Value="12" />
          </Style>
          <Style x:Key="DsWarningBox" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource SystemFillColorCautionBackgroundBrush}" />
            <Setter Property="BorderBrush" Value="{ThemeResource SystemFillColorCautionBrush}" />
            <Setter Property="BorderThickness" Value="{ThemeResource DsBadgeBorderThickness}" />
            <Setter Property="CornerRadius" Value="8" />
            <Setter Property="Padding" Value="12,8" />
          </Style>

          <Style x:Key="DsBadgeNeutral" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource SystemFillColorNeutralBackgroundBrush}" />
            <Setter Property="BorderBrush" Value="{ThemeResource TextFillColorSecondaryBrush}" />
            <Setter Property="BorderThickness" Value="{ThemeResource DsBadgeBorderThickness}" />
            <Setter Property="CornerRadius" Value="4" />
            <Setter Property="Padding" Value="6,1" />
          </Style>
          <Style x:Key="DsBadgeAccent" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource SystemFillColorAttentionBackgroundBrush}" />
            <Setter Property="BorderBrush" Value="{ThemeResource AccentTextFillColorPrimaryBrush}" />
            <Setter Property="BorderThickness" Value="{ThemeResource DsBadgeBorderThickness}" />
            <Setter Property="CornerRadius" Value="4" />
            <Setter Property="Padding" Value="6,1" />
          </Style>
          <Style x:Key="DsBadgeGood" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource SystemFillColorSuccessBackgroundBrush}" />
            <Setter Property="BorderBrush" Value="{ThemeResource SystemFillColorSuccessBrush}" />
            <Setter Property="BorderThickness" Value="{ThemeResource DsBadgeBorderThickness}" />
            <Setter Property="CornerRadius" Value="4" />
            <Setter Property="Padding" Value="6,1" />
          </Style>
          <Style x:Key="DsBadgeCaution" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource SystemFillColorCautionBackgroundBrush}" />
            <Setter Property="BorderBrush" Value="{ThemeResource SystemFillColorCautionBrush}" />
            <Setter Property="BorderThickness" Value="{ThemeResource DsBadgeBorderThickness}" />
            <Setter Property="CornerRadius" Value="4" />
            <Setter Property="Padding" Value="6,1" />
          </Style>
          <Style x:Key="DsBadgeCritical" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource SystemFillColorCriticalBackgroundBrush}" />
            <Setter Property="BorderBrush" Value="{ThemeResource SystemFillColorCriticalBrush}" />
            <Setter Property="BorderThickness" Value="{ThemeResource DsBadgeBorderThickness}" />
            <Setter Property="CornerRadius" Value="4" />
            <Setter Property="Padding" Value="6,1" />
          </Style>
          <Style x:Key="DsBadgeTextNeutral" TargetType="TextBlock">
            <Setter Property="FontSize" Value="12" />
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}" />
          </Style>
          <Style x:Key="DsBadgeTextAccent" TargetType="TextBlock">
            <Setter Property="FontSize" Value="12" />
            <Setter Property="Foreground" Value="{ThemeResource AccentTextFillColorPrimaryBrush}" />
          </Style>
          <Style x:Key="DsBadgeTextGood" TargetType="TextBlock">
            <Setter Property="FontSize" Value="12" />
            <Setter Property="Foreground" Value="{ThemeResource SystemFillColorSuccessBrush}" />
          </Style>
          <Style x:Key="DsBadgeTextCaution" TargetType="TextBlock">
            <Setter Property="FontSize" Value="12" />
            <Setter Property="Foreground" Value="{ThemeResource SystemFillColorCautionBrush}" />
          </Style>
          <Style x:Key="DsBadgeTextCritical" TargetType="TextBlock">
            <Setter Property="FontSize" Value="12" />
            <Setter Property="Foreground" Value="{ThemeResource SystemFillColorCriticalBrush}" />
          </Style>
        </ResourceDictionary>
        """;

    /// <summary>Parses the dictionary and merges it after the Fluent control resources.</summary>
    public static void Load(Application app)
    {
        var dictionary = (ResourceDictionary)XamlReader.Load(Xaml);
        app.Resources.MergedDictionaries.Add(dictionary);
    }
}
