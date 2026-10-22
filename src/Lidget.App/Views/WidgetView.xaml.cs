using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lidget.App.ViewModels;

namespace Lidget.App.Views;

public partial class WidgetView : UserControl
{
    public WidgetView(UsageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.VisibleProviders.CollectionChanged += OnVisibleProvidersChanged;
        Unloaded += (_, _) => viewModel.VisibleProviders.CollectionChanged -= OnVisibleProvidersChanged;
        UpdateEmptyState();
    }

    /// <summary>Raised when the user clicks the widget with the left mouse button.</summary>
    public event EventHandler? Clicked;

    private void OnVisibleProvidersChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void UpdateEmptyState()
    {
        var viewModel = (UsageViewModel)DataContext;
        EmptyText.Visibility = viewModel.VisibleProviders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSurfaceClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Clicked?.Invoke(this, EventArgs.Empty);
    }
}
