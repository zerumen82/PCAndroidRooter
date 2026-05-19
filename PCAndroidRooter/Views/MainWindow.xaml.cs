using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PCAndroidRooter.Services;
using PCAndroidRooter.ViewModels;

namespace PCAndroidRooter.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        var adbService = new AdbService();
        var magiskService = new MagiskService();
        _viewModel = new MainViewModel(adbService, magiskService);
        DataContext = _viewModel;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _viewModel.MagiskService.Log += msg =>
        {
            Dispatcher.Invoke(() => _viewModel.AppendLog(msg));
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LogText))
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (LogScrollViewer != null && LogScrollViewer.ViewportHeight < LogScrollViewer.ExtentHeight)
                    LogScrollViewer.ScrollToBottom();
            });
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            _viewModel.AppendLog($"[ERROR FATAL] {ex.Message}");
            _viewModel.StatusText = $"Error: {ex.Message}";
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _viewModel.Shutdown();
    }
}
