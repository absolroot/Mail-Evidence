using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace MailViewer.App;

public sealed class SearchableComboBox : ComboBox
{
    private TextBox? _search;
    private FrameworkElement? _searchPanel;
    private TextBlock? _empty;

    public override void OnApplyTemplate()
    {
        if (_search is not null)
        {
            _search.TextChanged -= OnFilterChanged;
            _search.PreviewKeyDown -= OnSearchKeyDown;
        }
        base.OnApplyTemplate();
        _search = GetTemplateChild("PART_Search") as TextBox;
        _searchPanel = GetTemplateChild("SearchPanel") as FrameworkElement;
        _empty = GetTemplateChild("EmptyOptions") as TextBlock;
        if (_search is not null)
        {
            _search.TextChanged += OnFilterChanged;
            _search.PreviewKeyDown += OnSearchKeyDown;
        }
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        if (IsDropDownOpen) ApplyFilter();
    }

    protected override void OnDropDownOpened(EventArgs e)
    {
        base.OnDropDownOpened(e);
        if (_searchPanel is null || _search is null || _empty is null) return;
        bool searchable = Items.Count > 3;
        _searchPanel.Visibility = searchable ? Visibility.Visible : Visibility.Collapsed;
        bool english = Language.IetfLanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        string label = english ? "Search options" : "목록에서 검색";
        AutomationProperties.SetName(_search, label);
        _search.ToolTip = label;
        _empty.Text = english ? "No matching options." : "일치하는 항목이 없습니다.";
        if (!searchable) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsDropDownOpen) _search.Focus();
        }));
    }

    protected override void OnDropDownClosed(EventArgs e)
    {
        if (_search is not null) _search.Clear();
        base.OnDropDownClosed(e);
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is ComboBoxItem option) SetVisibility(option, item);
    }

    private bool Matches(object item) => _search is null || _search.Text.Trim() is not { Length: > 0 } query ||
        CultureInfo.CurrentCulture.CompareInfo.IndexOf(
            GetItemText(item), query, CompareOptions.IgnoreCase) >= 0;

    private string GetItemText(object item)
    {
        object? value = item is ComboBoxItem option ? option.Content : item;
        if (value is not null && TextSearch.GetTextPath(this) is { Length: > 0 } path)
            value = TypeDescriptor.GetProperties(value).Find(path, ignoreCase: false)?.GetValue(value);
        return value?.ToString() ?? "";
    }

    private void SetVisibility(ComboBoxItem option, object item) =>
        option.Visibility = Matches(item) ? Visibility.Visible : Visibility.Collapsed;

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        for (int index = 0; index < Items.Count; index++)
            if (ItemContainerGenerator.ContainerFromIndex(index) is ComboBoxItem option)
                SetVisibility(option, Items[index]);
        if (_empty is not null)
            _empty.Visibility = Items.Cast<object>().Any(Matches) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Down or Key.Escape)) return;
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            IsDropDownOpen = false;
            Focus();
            return;
        }
        var matches = Items.Cast<object>().Where(Matches);
        string query = _search?.Text.Trim() ?? "";
        object? match = query.Length == 0 && e.Key == Key.Enter && SelectedItem is not null ? SelectedItem :
            matches.FirstOrDefault(item => GetItemText(item).Equals(query, StringComparison.CurrentCultureIgnoreCase)) ??
            matches.FirstOrDefault();
        if (match is null) return;
        if (e.Key == Key.Enter)
        {
            SelectedItem = match;
            IsDropDownOpen = false;
            Focus();
        }
        else if (ItemContainerGenerator.ContainerFromItem(match) is ComboBoxItem option)
        {
            option.BringIntoView();
            option.Focus();
        }
    }
}
