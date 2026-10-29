using System.Windows;
using HotelKeeper.ViewModels;

namespace HotelKeeper.App.Views.Shared;

public partial class FormDialog : Window
{
    public FormDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is FormDialogViewModel { IsDanger: true }) OkButton.Style = (Style)FindResource("DangerBtn");
        };
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is FormDialogViewModel vm && vm.Submit()) DialogResult = true;
    }
}
