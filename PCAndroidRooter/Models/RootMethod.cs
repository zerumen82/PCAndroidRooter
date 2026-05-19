using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PCAndroidRooter.Models;

public enum RootMethodType
{
    MagiskPatch,
    BootloaderUnlock,
    AdbExploit,
    CustomRecovery
}

public enum RootMethodStatus
{
    Ready,
    Running,
    Success,
    Failed,
    NotSupported,
    WaitingDevice
}

public partial class RootMethod : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public RootMethodType Type { get; set; }
    public string Icon { get; set; } = "";

    private RootMethodStatus _status = RootMethodStatus.Ready;
    public RootMethodStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
                OnPropertyChanged(nameof(StatusColor));
        }
    }

    public Brush StatusColor => Status switch
    {
        RootMethodStatus.Ready => new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
        RootMethodStatus.Running => new SolidColorBrush(Color.FromRgb(0x42, 0xA5, 0xF5)),
        RootMethodStatus.Success => new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A)),
        RootMethodStatus.Failed => new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50)),
        RootMethodStatus.NotSupported => new SolidColorBrush(Color.FromRgb(0xFF, 0xA7, 0x26)),
        RootMethodStatus.WaitingDevice => new SolidColorBrush(Color.FromRgb(0xFF, 0xA7, 0x26)),
        _ => new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB))
    };

    public string StatusText => Status switch
    {
        RootMethodStatus.Ready => "Listo",
        RootMethodStatus.Running => "Ejecutando...",
        RootMethodStatus.Success => "Completado",
        RootMethodStatus.Failed => "Falló",
        RootMethodStatus.NotSupported => "No soportado",
        RootMethodStatus.WaitingDevice => "Esperando dispositivo...",
        _ => "Desconocido"
    };

    public bool IsAvailable => Status != RootMethodStatus.NotSupported;
}
