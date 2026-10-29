using System.Windows;
using HotelKeeper.ViewModels;

namespace HotelKeeper.App.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LoginViewModel vm)
            {
                vm.PasswordProvider = () => PasswordBox.Password;
                vm.ClearPassword = () => PasswordBox.Clear();
            }
        };
        Loaded += (_, _) => UserBox.Focus();
    }
}
