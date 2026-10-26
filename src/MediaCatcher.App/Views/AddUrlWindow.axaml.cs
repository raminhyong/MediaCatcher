using Avalonia;
using Avalonia.Controls;
using MediaCatcher.App.ViewModels;

namespace MediaCatcher.App.Views;

public partial class AddUrlWindow : Window
{
    private AddUrlViewModel? _viewModel;

    public AddUrlWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
        }

        _viewModel = DataContext as AddUrlViewModel;
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += OnCloseRequested;
        }

        base.OnDataContextChanged(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UrlBox.Focus();
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

    private void OnCloseRequested(AddUrlResult? result) => Close(result);
}
