using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HotelKeeper.App.Views.Shared;

public partial class TitleBar : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(TitleBar), new PropertyMetadata(""));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty ShowMinMaxProperty =
        DependencyProperty.Register(nameof(ShowMinMax), typeof(bool), typeof(TitleBar), new PropertyMetadata(true, OnChromeChanged));

    public bool ShowMinMax
    {
        get => (bool)GetValue(ShowMinMaxProperty);
        set => SetValue(ShowMinMaxProperty, value);
    }

    static void OnChromeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((TitleBar)d).ApplyChrome();

    Window? win;

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        win = Window.GetWindow(this);
        if (win != null)
        {
            win.StateChanged += (_, _) => UpdateMaxGlyph();
            UpdateMaxGlyph();
        }
        ApplyChrome();
    }

    void ApplyChrome()
    {
        MinButton.Visibility = ShowMinMax ? Visibility.Visible : Visibility.Collapsed;
        MaxButton.Visibility = ShowMinMax ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>WindowStyle=None windows would otherwise cover the taskbar when maximized, so the bounds are clamped
    /// to the work area while maximized and released again on restore.</summary>
    void UpdateMaxGlyph()
    {
        var maximized = win?.WindowState == WindowState.Maximized;
        MaxGlyph.Text = maximized ? "" : "";
        MaxButton.ToolTip = maximized ? "Restore" : "Maximize";
        if (win is null) return;
        if (maximized)
        {
            win.MaxWidth = SystemParameters.WorkArea.Width;
            win.MaxHeight = SystemParameters.WorkArea.Height;
        }
        else
        {
            win.MaxWidth = double.PositiveInfinity;
            win.MaxHeight = double.PositiveInfinity;
        }
    }

    void Min_Click(object sender, RoutedEventArgs e)
    {
        if (win != null) win.WindowState = WindowState.Minimized;
    }

    void Max_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    void Close_Click(object sender, RoutedEventArgs e) => win?.Close();

    void ToggleMaximize()
    {
        if (win == null) return;
        win.WindowState = win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    void Bar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (win is null || FromButton(e.OriginalSource)) return;
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (win.WindowState == WindowState.Maximized) return;
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { win.DragMove(); } catch (InvalidOperationException) { /* mouse released mid-drag */ }
        }
    }

    static bool FromButton(object? source)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is Button) return true;
        return false;
    }
}
