using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PixelBridge.App.Controls;

/// <summary>Windows Settings-style row: glyph, header, optional description, control on the right.</summary>
public sealed class SettingCard : ContentControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingCard));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingCard));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingCard), new PropertyMetadata(""));

    public string? Header { get => (string?)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
}

public sealed class NullToCollapsed : IValueConverter
{
    public static readonly NullToCollapsed Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Signal bars (0–4) as a Segoe Fluent Icons glyph.</summary>
public sealed class BarsToGlyph : IValueConverter
{
    public static readonly BarsToGlyph Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as int? ?? 0) switch
    {
        4 => "\uE701", 3 => "\uE874", 2 => "\uE873", 1 => "\uE872", 0 => "\uE871", _ => "",
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisible : IValueConverter
{
    public static readonly BoolToVisible Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) ^ (parameter as string == "invert") ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
