using System.Windows;

namespace PCAndroidRooter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show($"Error inesperado: {args.Exception.Message}\n\nLa aplicación continuará.", "PC Android Rooter", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
