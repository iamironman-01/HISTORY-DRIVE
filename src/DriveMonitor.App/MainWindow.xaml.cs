using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using DriveMonitor.App.Services;
using DriveMonitor.App.ViewModels;

namespace DriveMonitor.App;

/// <summary>
/// The main window. Everything it shows comes from <see cref="MainViewModel"/>; the code here only
/// does what needs the visual tree: scroll anchoring, column sorting, row mouse handling, device
/// notifications (which need the window handle) and the window bounds.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private DeviceChangeWatcher? _deviceWatcher;
    private ScrollViewer? _activityScroll;
    private object? _anchorItem;
    private int _anchorIndex;
    private bool _rightClickOnRow;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ApplySavedBounds(viewModel.Settings);

        // A grid over its own collection view otherwise ties its selection to the view's current item, and every
        // Reset (a refresh with more than 200 new rows, a search, a re-sort) then collapses a multi-row selection
        // to that one row. Nothing here uses currency, so each grid keeps the rows the user selected instead.
        foreach (DataGrid grid in new[] { ActivityGrid, FilesGrid, ProcessesGrid })
        {
            grid.IsSynchronizedWithCurrentItem = false;
            grid.SelectionChanged += OnGridSelectionChanged;
        }

        viewModel.CloseRequested += OnCloseRequested;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.Activity.RowsChanging += OnActivityRowsChanging;
        viewModel.Activity.RowsChanged += OnActivityRowsChanged;
        Loaded += OnLoaded;
    }

    /// <summary>Stores the window position and the left panel width in the settings.</summary>
    internal void CaptureBounds()
    {
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (!bounds.IsEmpty && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top) && bounds.Width > 0 && bounds.Height > 0)
        {
            _viewModel.Settings.Window = new WindowBounds
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                IsMaximized = WindowState == WindowState.Maximized,
            };
        }

        if (LeftColumn.ActualWidth > 0)
            _viewModel.Settings.LeftPanelWidth = LeftColumn.ActualWidth;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _deviceWatcher = new DeviceChangeWatcher(this);
        _deviceWatcher.VolumesChanged += OnVolumesChanged;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
            return;

        CaptureBounds();

        // While the window still has its handle.
        _deviceWatcher?.Dispose();
    }

    protected override void OnClosed(EventArgs e)
    {
        _deviceWatcher?.Dispose();
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Activity.RowsChanging -= OnActivityRowsChanging;
        _viewModel.Activity.RowsChanged -= OnActivityRowsChanged;
        base.OnClosed(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "initialize");
            _viewModel.ReportBackgroundError(ex);
        }
    }

    private void OnVolumesChanged(object? sender, EventArgs e) => _viewModel.OnVolumesChanged();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Turning auto-scroll on jumps to the newest row at once rather than at the next event.
        if (e.PropertyName == nameof(MainViewModel.AutoScroll) && FollowsNewest && ActivityGrid.IsVisible)
            ScrollToNewest();
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void ApplySavedBounds(AppSettings settings)
    {
        LeftColumn.Width = new GridLength(settings.LeftPanelWidth);
        if (settings.Window is not { } saved)
            return;

        double width = Math.Max(saved.Width, MinWidth);
        double height = Math.Max(saved.Height, MinHeight);
        var screen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        Rect visible = Rect.Intersect(new Rect(saved.Left, saved.Top, width, height), screen);

        // A monitor that was unplugged since: keep the size, centre on the primary screen.
        if (visible.IsEmpty || visible.Width < 200 || visible.Height < 100)
        {
            Width = Math.Min(width, screen.Width);
            Height = Math.Min(height, screen.Height);
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = saved.Left;
        Top = saved.Top;
        Width = width;
        Height = height;
        if (saved.IsMaximized)
            WindowState = WindowState.Maximized;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>Opens the button's drop-down menu below it (Export, log options).</summary>
    private void OnMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.DataContext = button.DataContext;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbles up from the grids too.
        if (!ReferenceEquals(e.OriginalSource, MainTabs))
            return;

        // The Live activity grid did not follow new rows while hidden; show the newest now.
        if (MainTabs.SelectedIndex == 0 && FollowsNewest)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ScrollToNewest));
    }

    // Live activity scroll handling. Rows are appended at the end and the oldest are removed from the
    // start. With auto-scroll on, the newest row is brought into view once per batch; otherwise the
    // first visible row stays where it is, however many rows were removed above it.

    private bool IsActivitySorted => _viewModel.Activity.View.CustomSort is not null || _viewModel.Activity.View.SortDescriptions.Count > 0;

    /// <summary>Auto-scroll follows arrival order; in a sorted view the newest row is not the last one.</summary>
    private bool FollowsNewest => _viewModel.AutoScroll && !IsActivitySorted;

    private void OnActivityRowsChanging(object? sender, EventArgs e)
    {
        _anchorItem = null;
        if (FollowsNewest || !ActivityGrid.IsVisible || ActivityScroll() is not { } scroll)
            return;

        int first = (int)scroll.VerticalOffset;
        if (first >= 0 && first < ActivityGrid.Items.Count)
        {
            _anchorIndex = first;
            _anchorItem = ActivityGrid.Items[first];
        }
    }

    private void OnActivityRowsChanged(object? sender, EventArgs e)
    {
        object? anchor = _anchorItem;
        _anchorItem = null;
        if (!ActivityGrid.IsVisible)
            return;

        if (FollowsNewest)
        {
            ScrollToNewest();
            return;
        }

        if (anchor is null || ActivityScroll() is not { } scroll)
            return;
        int index = ActivityGrid.Items.IndexOf(anchor);
        if (index != _anchorIndex)
            scroll.ScrollToVerticalOffset(Math.Max(index, 0)); // -1: the anchor itself was removed
    }

    /// <summary>Scrolls the last visible row of Live activity into view.</summary>
    private void ScrollToNewest()
    {
        int count = ActivityGrid.Items.Count;
        if (count > 0)
            ActivityGrid.ScrollIntoView(ActivityGrid.Items[count - 1]);
    }

    private ScrollViewer? ActivityScroll() => _activityScroll ??= FindDescendant<ScrollViewer>(ActivityGrid);

    // Column sorting: a third click on a header returns to arrival order. Typed comparers replace
    // WPF's reflection-based sorting, which is slow on large tables.

    private void OnGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (sender is not DataGrid grid || grid.ItemsSource is not ListCollectionView view)
            return;

        DataGridColumn column = e.Column;
        ListSortDirection? direction = column.SortDirection switch
        {
            null => ListSortDirection.Ascending,
            ListSortDirection.Ascending => ListSortDirection.Descending,
            _ => null,
        };

        IComparer? comparer = null;
        if (direction is { } d)
        {
            string member = column.SortMemberPath;
            comparer = ReferenceEquals(grid, ActivityGrid) ? RowSorting.ForActivity(member, d)
                : ReferenceEquals(grid, FilesGrid) ? RowSorting.ForFiles(member, d)
                : RowSorting.ForProcesses(member, d);
            if (comparer is null)
                return; // not a sortable column
        }

        foreach (DataGridColumn other in grid.Columns)
            other.SortDirection = null;
        column.SortDirection = direction;
        view.CustomSort = comparer;
    }

    /// <summary>
    /// Tells the table how many rows its grid has selected: with thousands selected, adding or removing rows one
    /// notification at a time costs that many times more, so the table switches to a Reset sooner.
    /// </summary>
    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection changes of controls inside the grid bubble up through it too.
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not DataGrid grid)
            return;

        int selected = grid.SelectedItems.Count;
        if (ReferenceEquals(grid, ActivityGrid))
            _viewModel.Activity.SelectedCount = selected;
        else if (ReferenceEquals(grid, FilesGrid))
            _viewModel.Files.SelectedCount = selected;
        else
            _viewModel.Processes.SelectedCount = selected;
    }

    // Row mouse handling: right-click selects the row under the mouse; double-click opens its folder.

    private void OnGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DataGridRow? row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        _rightClickOnRow = row is not null;
        if (row is null || row.IsSelected || sender is not DataGrid grid)
            return;

        // Right-clicking outside the selection acts on the clicked row only, as in File Explorer.
        grid.SelectedItem = row.Item;
    }

    private void OnGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;

        // Opened by mouse over a header or the empty area, or with nothing selected: nothing to act on.
        bool byMouse = e.CursorLeft >= 0 || e.CursorTop >= 0;
        bool onRows = grid.SelectedItems.Count > 0 && (!byMouse || _rightClickOnRow);
        _rightClickOnRow = false;
        if (!onRows)
        {
            e.Handled = true;
            return;
        }

        // The commands act on the rows selected when the menu opened. The grid's own SelectedItems keeps changing
        // while the menu is open: Live activity removes its oldest rows, and a removed row leaves the selection.
        if (grid.ContextMenu is { } menu)
        {
            List<object> rows = grid.SelectedItems.Cast<object>().ToList();
            foreach (MenuItem item in menu.Items.OfType<MenuItem>())
                item.CommandParameter = rows;
        }
    }

    private void OnGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        DataGridRow? row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null)
            return; // a header or the scroll bar

        if (ReferenceEquals(sender, ProcessesGrid))
            _viewModel.FilterToProcess(row.Item);
        else
            _viewModel.OpenContainingFolder(row.Item);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null and not T)
        {
            // Text runs are content elements, which are not part of the visual tree.
            element = element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return element as T;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }

        return null;
    }
}
