using EmbyRemotePlayer.Services;
using System.Windows.Data;
using System.Windows.Controls.Primitives;

namespace EmbyRemotePlayer.Views;

public partial class DebugConsoleWindow : Window
{
    private readonly ObservableCollection<NetworkDebugEntry> _entries = new();
    private ICollectionView? _view;
    private string _resourceFilter = "all";

    public DebugConsoleWindow()
    {
        InitializeComponent();
    }

    public static void ShowFor(Window owner)
    {
        var existing = Application.Current.Windows.OfType<DebugConsoleWindow>().FirstOrDefault();
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Show();
            existing.Activate();
            return;
        }

        var window = new DebugConsoleWindow { Owner = owner };
        window.Show();
        window.Activate();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        NetworkDebugService.BeginBodyCapture();
        foreach (var entry in NetworkDebugService.GetEntriesSnapshot()) _entries.Add(entry);
        _view = CollectionViewSource.GetDefaultView(_entries);
        _view.Filter = FilterEntry;
        RequestGrid.ItemsSource = _view;
        NetworkDebugService.EntryChanged += NetworkDebugService_EntryChanged;
        NetworkDebugService.Cleared += NetworkDebugService_Cleared;
        UpdateSummary();
        if (_entries.Count > 0)
        {
            RequestGrid.SelectedItem = _entries[^1];
            RequestGrid.ScrollIntoView(_entries[^1]);
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        NetworkDebugService.EntryChanged -= NetworkDebugService_EntryChanged;
        NetworkDebugService.Cleared -= NetworkDebugService_Cleared;
        NetworkDebugService.EndBodyCapture();
    }

    private void NetworkDebugService_EntryChanged(NetworkDebugEntry entry)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            var existing = _entries.FirstOrDefault(x => ReferenceEquals(x, entry));
            if (existing is null) _entries.Add(entry);
            _view?.Refresh();
            UpdateSummary();
            if (ReferenceEquals(RequestGrid.SelectedItem, entry)) ShowDetails(entry);
        }), DispatcherPriority.Background);
    }

    private void NetworkDebugService_Cleared()
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            _entries.Clear();
            ClearDetails();
            UpdateSummary();
        }), DispatcherPriority.Background);
    }

    private bool FilterEntry(object item)
    {
        if (item is not NetworkDebugEntry entry) return false;
        if (_resourceFilter == "errors" && !entry.IsError) return false;
        if (_resourceFilter is not "all" and not "errors" && !entry.ResourceType.Equals(_resourceFilter, StringComparison.OrdinalIgnoreCase)) return false;

        var search = FilterTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(search)) return true;
        return entry.Url.Contains(search, StringComparison.OrdinalIgnoreCase)
               || entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
               || entry.Method.Contains(search, StringComparison.OrdinalIgnoreCase)
               || entry.StatusDisplay.Contains(search, StringComparison.OrdinalIgnoreCase)
               || entry.TypeDisplay.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (FilterHint is not null) FilterHint.Visibility = string.IsNullOrEmpty(FilterTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _view?.Refresh();
        UpdateSummary();
    }

    private void ResourceFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton selected) return;
        _resourceFilter = selected.Tag?.ToString() ?? "all";
        foreach (var button in FindVisualChildren<ToggleButton>(this).Where(b => b.Tag is not null))
            button.IsChecked = ReferenceEquals(button, selected);
        _view?.Refresh();
        UpdateSummary();
    }

    private void RequestGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RequestGrid.SelectedItem is NetworkDebugEntry entry) ShowDetails(entry);
        else ClearDetails();
    }

    private void ShowDetails(NetworkDebugEntry entry)
    {
        SelectedMethodText.Text = entry.Method;
        SelectedStatusText.Text = entry.StatusCode is int code ? $"{code} {entry.StatusText}" : entry.StatusText;
        SelectedStatusText.Foreground = entry.IsError
            ? new SolidColorBrush(Color.FromRgb(242, 139, 130))
            : entry.StatusCode is >= 200 and < 400
                ? new SolidColorBrush(Color.FromRgb(129, 201, 149))
                : new SolidColorBrush(Color.FromRgb(148, 154, 167));
        SelectedUrlTextBox.Text = entry.Url;
        HeadersTextBox.Text = entry.HeadersText;
        PayloadTextBox.Text = entry.PayloadText;
        ResponseTextBox.Text = string.IsNullOrWhiteSpace(entry.ResponseBody) ? "<empty or pending>" : entry.ResponseBody;
        TimingTextBox.Text = entry.TimingText;
    }

    private void ClearDetails()
    {
        SelectedMethodText.Text = "—";
        SelectedStatusText.Text = "选择一个请求查看详情";
        SelectedStatusText.Foreground = new SolidColorBrush(Color.FromRgb(148, 154, 167));
        SelectedUrlTextBox.Clear();
        HeadersTextBox.Clear();
        PayloadTextBox.Clear();
        ResponseTextBox.Clear();
        TimingTextBox.Clear();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => NetworkDebugService.Clear();

    private void CopyCurlButton_Click(object sender, RoutedEventArgs e)
    {
        if (RequestGrid.SelectedItem is not NetworkDebugEntry entry) return;
        TrySetClipboard(entry.ToCurl());
    }

    private void CopyDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (RequestGrid.SelectedItem is not NetworkDebugEntry entry) return;
        var report = new StringBuilder()
            .AppendLine("=== Headers ===").AppendLine(entry.HeadersText)
            .AppendLine().AppendLine("=== Payload ===").AppendLine(entry.PayloadText)
            .AppendLine().AppendLine("=== Response ===").AppendLine(string.IsNullOrWhiteSpace(entry.ResponseBody) ? "<empty or pending>" : entry.ResponseBody)
            .AppendLine().AppendLine("=== Timing ===").AppendLine(entry.TimingText)
            .ToString();
        TrySetClipboard(report);
    }

    private void CopyResponseButton_Click(object sender, RoutedEventArgs e)
    {
        if (RequestGrid.SelectedItem is not NetworkDebugEntry entry || string.IsNullOrEmpty(entry.ResponseBody)) return;
        TrySetClipboard(entry.ResponseBody);
    }

    private static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex) { LogService.Warning("Unable to copy network inspector contents.", ex); }
    }

    private void UpdateSummary()
    {
        var visibleCount = _view?.Cast<object>().Count() ?? _entries.Count;
        var errors = _entries.Count(e => e.IsError);
        var totalBytes = _entries.Where(e => e.ResponseSize.HasValue).Sum(e => e.ResponseSize ?? 0);
        SummaryText.Text = $"{visibleCount} / {_entries.Count} requests   ·   {errors} errors   ·   {FormatBytes(totalBytes)}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        return $"{bytes / 1024d / 1024d:0.0} MB";
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is null) yield break;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
}
