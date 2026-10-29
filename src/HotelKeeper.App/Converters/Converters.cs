using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace HotelKeeper.App.Converters;

/// <summary>Icon key ("bed") to the line-icon geometry defined in Resources/Icons/Icons.xaml.</summary>
public sealed class IconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value as string;
        if (string.IsNullOrEmpty(key)) return Geometry.Empty;
        return Application.Current.TryFindResource("Icon." + key) as Geometry ?? Geometry.Empty;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True becomes Collapsed, False becomes Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Status text to a soft background colour for badges.</summary>
public sealed class BadgeBrushConverter : IValueConverter
{
    static readonly string[] Good = { "available", "confirmed", "fulfilled", "completed", "active", "closed", "checked in", "in house", "paid", "good", "resolved", "yes" };
    static readonly string[] Warn = { "pending", "dirty", "cleaning", "acknowledged", "in progress", "high", "assigned", "open", "worn", "registered" };
    static readonly string[] Bad = { "out of service", "cancelled", "rejected", "unable to fulfill", "urgent", "damaged", "missing", "disabled", "archived", "safety", "major" };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var t = (value?.ToString() ?? "").Trim().ToLowerInvariant();
        string key = Good.Contains(t) ? "SuccessBackBrush" : Warn.Contains(t) ? "WarnBackBrush" : Bad.Contains(t) ? "DangerBackBrush" : "NeutralBackBrush";
        return Application.Current.TryFindResource(key) ?? Brushes.LightGray;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Empty string hides the element.</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
