using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using PCAndroidRooter.Services;
using PCAndroidRooter.ViewModels;

namespace PCAndroidRooter.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private Storyboard? spinStoryboard;

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

        if (e.PropertyName == nameof(MainViewModel.IsRooting))
        {
            Dispatcher.InvokeAsync(() =>
            {
                var spinAnimation = (Storyboard)FindResource("SpinAnimation");
                if (_viewModel.IsRooting)
                {
                    spinStoryboard = spinAnimation;
                    spinStoryboard?.Begin(this, true);
                }
                else
                {
                    spinStoryboard?.Stop(this);
                    SpinTransform.Angle = 0;
                }
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
        if (_viewModel.IsRooting)
        {
            var result = MessageBox.Show(
                "Hay una operación de root/restauración en curso.\n\n" +
                "Si sales ahora, el proceso se cancelará (el teléfono puede quedar en un estado intermedio).\n\n" +
                "¿Salir de todos modos?",
                "Operación en curso",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.OK)
            {
                e.Cancel = true;
                return;
            }
        }
        _viewModel.Shutdown();
    }
}
