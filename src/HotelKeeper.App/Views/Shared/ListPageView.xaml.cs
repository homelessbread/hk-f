using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using HotelKeeper.ViewModels;

namespace HotelKeeper.App.Views.Shared;

public partial class ListPageView : UserControl
{
    public ListPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BuildColumns();
    }

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ListPageViewModel vm && vm.SearchCommand.CanExecute(null)) vm.SearchCommand.Execute(null);
    }

    void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        RowContextMenu.Items.Clear();
        if (DataContext is not ListPageViewModel vm) return;

        foreach (var action in vm.Actions)
        {
            if (action.NeedsSelection && vm.Selected is null) continue;
            if (!action.Command.CanExecute(null)) continue;

            var item = new MenuItem
            {
                Header = action.Label,
                Command = action.Command,
                ToolTip = string.IsNullOrEmpty(action.Tip) ? null : action.Tip
            };
            RowContextMenu.Items.Add(item);
        }

        if (RowContextMenu.Items.Count == 0)
        {
            e.Handled = true;
        }
    }

    void BuildColumns()
    {
        Grid.Columns.Clear();
        if (DataContext is not ListPageViewModel vm) return;
        foreach (var c in vm.Columns)
        {
            var width = c.Width > 0 ? new DataGridLength(c.Width) : new DataGridLength(1, DataGridLengthUnitType.Auto);
            DataGridColumn col;
            switch (c.Kind)
            {
                case ColumnKind.Badge:
                {
                    var factory = new FrameworkElementFactory(typeof(Border));
                    factory.SetBinding(Border.BackgroundProperty, new Binding(c.Path) { Converter = (IValueConverter)FindResource("BadgeBrush") });
                    factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
                    factory.SetValue(Border.PaddingProperty, new Thickness(9, 2, 9, 2));
                    factory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
                    var text = new FrameworkElementFactory(typeof(TextBlock));
                    text.SetBinding(TextBlock.TextProperty, new Binding(c.Path));
                    text.SetValue(TextBlock.FontSizeProperty, 12.0);
                    factory.AppendChild(text);
                    col = new DataGridTemplateColumn { CellTemplate = new DataTemplate { VisualTree = factory } };
                    break;
                }
                case ColumnKind.Money:
                    col = new DataGridTextColumn { Binding = new Binding(c.Path) { StringFormat = "N2", ConverterCulture = CultureInfo.CurrentCulture } };
                    break;
                case ColumnKind.Date:
                    col = new DataGridTextColumn { Binding = new Binding(c.Path) { StringFormat = "dd MMM yyyy", ConverterCulture = CultureInfo.CurrentCulture } };
                    break;
                case ColumnKind.DateTime:
                    col = new DataGridTextColumn { Binding = new Binding(c.Path) { StringFormat = "dd MMM yyyy HH:mm", ConverterCulture = CultureInfo.CurrentCulture } };
                    break;
                case ColumnKind.Bool:
                    col = new DataGridCheckBoxColumn { Binding = new Binding(c.Path) };
                    break;
                default:
                    col = new DataGridTextColumn { Binding = new Binding(c.Path) };
                    break;
            }
            col.Header = c.Header;
            col.Width = width;
            Grid.Columns.Add(col);
        }
    }
}