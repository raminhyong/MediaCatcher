using Avalonia;
using Avalonia.Controls;
using MediaCatcher.App.ViewModels;

namespace MediaCatcher.App.Views;

public partial class FirstRunWindow : Window
{
    private FirstRunViewModel? _viewModel;

    public FirstRunWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
        }

        _viewModel = DataContext as FirstRunViewModel;
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += OnCloseRequested;
        }

        base.OnDataContextChanged(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
            _viewModel = null;
        }

        base.OnClosed(e);
    }

    private void OnCloseRequested() => Close();
}
