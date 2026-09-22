using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PCAndroidRooter.Models;

public enum RootMethodType
{
    MagiskPatch,
    BootloaderUnlock,
    AdbExploit,
    CustomRecovery,
    KernelSU,
    OneClickRoot,
    TemporaryRoot,
    FastbootBoot,
    MtkClientUnlock
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
    public string FriendlyName { get; set; } = string.Empty;
    public string FriendlyDescription { get; set; } = string.Empty;
    public RootMethodType Type { get; set; }
    public string Icon { get; set; } = "";
    public bool IsAdvanced { get; set; }
    public string Difficulty { get; set; } = "Fácil";
    public string RiskLevel { get; set; } = "Bajo";

    private RootMethodStatus _status = RootMethodStatus.Ready;
    public RootMethodStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(StatusText));
            }
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

    private bool _isRecommended;
    public bool IsRecommended
    {
        get => _isRecommended;
        set => SetProperty(ref _isRecommended, value);
    }
}
