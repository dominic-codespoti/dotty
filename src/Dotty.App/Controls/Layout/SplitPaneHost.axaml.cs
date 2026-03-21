using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Dotty.App.ViewModels;
using Dotty.App.Views;
using System.ComponentModel;

namespace Dotty.App.Controls.Layout;

public partial class SplitPaneHost : UserControl
{
    public SplitPaneHost()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        UpdateContent();
    }

    private void UpdateContent()
    {
        if (DataContext is TerminalViewModel tvm)
        {
            var terminalView = new TerminalView();
            terminalView.Session = tvm.Session;
            Content = terminalView;
        }
        else if (DataContext is SplitContainerViewModel scvm)
        {
            var grid = new Grid();
            
            if (scvm.IsHorizontal)
            {
                grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Need standard splitter size
                grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));

                var top = new SplitPaneHost { DataContext = scvm.FirstChild };
                Grid.SetRow(top, 0);

                var splitter = new GridSplitter { Background = Brushes.Gray, Height = 2, HorizontalAlignment = HorizontalAlignment.Stretch };
                Grid.SetRow(splitter, 1);

                var bottom = new SplitPaneHost { DataContext = scvm.SecondChild };
                Grid.SetRow(bottom, 2);

                grid.Children.Add(top);
                grid.Children.Add(splitter);
                grid.Children.Add(bottom);
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

                var left = new SplitPaneHost { DataContext = scvm.FirstChild };
                Grid.SetColumn(left, 0);

                var splitter = new GridSplitter { Background = Brushes.Gray, Width = 2, VerticalAlignment = VerticalAlignment.Stretch };
                Grid.SetColumn(splitter, 1);

                var right = new SplitPaneHost { DataContext = scvm.SecondChild };
                Grid.SetColumn(right, 2);

                grid.Children.Add(left);
                grid.Children.Add(splitter);
                grid.Children.Add(right);
            }

            Content = grid;
        }
    }
}
