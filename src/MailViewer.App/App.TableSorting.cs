using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace MailViewer.App;

public partial class App
{
    private void EnableEvidenceSorting(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        grid.Sorting -= SortEvidenceTable;
        grid.Sorting += SortEvidenceTable;
    }

    private static void SortEvidenceTable(object sender, DataGridSortingEventArgs e)
    {
        if (sender is not DataGrid { ItemsSource: { } items } grid
            || string.IsNullOrWhiteSpace(e.Column.SortMemberPath))
            return;

        var view = CollectionViewSource.GetDefaultView(items);
        if (!view.CanSort) return;
        e.Handled = true;
        bool? next = SortCycle.Next(e.Column.SortDirection.HasValue,
            e.Column.SortDirection == ListSortDirection.Descending);
        ListSortDirection? direction = next is { } descending
            ? descending ? ListSortDirection.Descending : ListSortDirection.Ascending
            : null;

        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            if (direction is { } sort)
                view.SortDescriptions.Add(new SortDescription(e.Column.SortMemberPath, sort));
            foreach (DataGridColumn column in grid.Columns)
                column.SortDirection = column == e.Column ? direction : null;
        }
    }
}
