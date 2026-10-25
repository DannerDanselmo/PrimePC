using System.Data;
using System.Windows.Controls;

namespace PrimePC.App.Views;

public partial class BottleneckView : UserControl
{
    public BottleneckView()
    {
        InitializeComponent();
    }

    /// <summary>As colunas da comparação têm nomes curtos (S1, S2…); o título real fica na legenda da coluna.</summary>
    private void OnComparisonColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (ComparisonGrid.ItemsSource is DataView view && view.Table?.Columns[e.PropertyName] is { } column)
            e.Column.Header = column.Caption;
        e.Column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
    }
}
