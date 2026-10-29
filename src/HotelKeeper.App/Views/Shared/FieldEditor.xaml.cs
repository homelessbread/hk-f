using System.Windows;
using System.Windows.Controls;
using HotelKeeper.ViewModels;

namespace HotelKeeper.App.Views.Shared;

public partial class FieldEditor : UserControl
{
    public FieldEditor() => InitializeComponent();

    // PasswordBox cannot be data-bound, so the typed password is copied into the field here.
    void Pwd_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is FormField f && f.IsPassword) f.Text = Pwd.Password;
    }
}
