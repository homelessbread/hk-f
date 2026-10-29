using System.Windows;

namespace HotelKeeper.App.Views.Shared;

public partial class MessageDialog : Window
{
    public MessageDialog(string title, string message, string okText, bool showCancel, bool isError, bool danger, IReadOnlyList<string>? lines)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        BodyText.Text = message;
        BodyText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        if (isError) TitleText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        OkButton.Content = okText;
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        if (danger) OkButton.Style = (Style)FindResource("DangerBtn");
        if (lines is { Count: > 0 }) LineList.ItemsSource = lines; else LineList.Visibility = Visibility.Collapsed;
        if (!showCancel) CancelButton.IsCancel = false;
    }

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
