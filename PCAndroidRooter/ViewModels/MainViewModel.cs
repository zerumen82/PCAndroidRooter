using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCAndroidRooter.Models;
using PCAndroidRooter.Services;

namespace PCAndroidRooter.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AdbService _adbService;
    private readonly MagiskService _magiskService;
    private readonly DeviceDetectionService _detectionService;
    private readonly RootService _rootService;
    private CancellationTokenSource? _rootCts;
    private readonly StringBuilder _logBuilder = new();
    private const int MaxLogLines = 500;
    private int _logLineCount;

    public MagiskService MagiskService => _magiskService;

    [ObservableProperty]
    private bool _isAdbReady;

    [ObservableProperty]
    private bool _isDeviceConnected;

    [ObservableProperty]
    private bool _isRooting;

    [ObservableProperty]
    private string _statusText = "Inicializando...";

    [ObservableProperty]
    private string _selectedSerial = string.Empty;

    [ObservableProperty]
    private double _adbProgress;

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private bool _autoScrollLog = true;

    [ObservableProperty]
    private string _deviceModel = "---";

    [ObservableProperty]
    private string _deviceManufacturer = "---";

    [ObservableProperty]
    private string _deviceAndroidVersion = "---";

    [ObservableProperty]
    private string _deviceBuild = "---";

    [ObservableProperty]
    private string _deviceBattery = "---";

    [ObservableProperty]
    private string _deviceAbi = "---";

    [ObservableProperty]
    private string _deviceSecurityPatch = "---";

    [ObservableProperty]
    private string _deviceRam = "---";

    [ObservableProperty]
    private bool _isDeviceRooted;

    [ObservableProperty]
    private bool _bootloaderUnlocked;

    [ObservableProperty]
    private string _connectionStatus = "Desconectado";

    public ObservableCollection<string> DeviceList { get; } = new();
    public ObservableCollection<RootMethod> RootMethods { get; } = new();

    public MainViewModel(AdbService adbService, MagiskService magiskService)
    {
        _adbService = adbService;
        _magiskService = magiskService;
        _detectionService = new DeviceDetectionService(adbService);
        _rootService = new RootService(adbService, magiskService);

        InitializeMethods();

        _adbService.OutputReceived += msg => AppendLog(msg);
        _adbService.ErrorReceived += msg => AppendLog($"[ERROR] {msg}");
        _adbService.CommandExecuting += msg => AppendLog(msg);
        _adbService.DownloadProgress += p =>
        {
            Application.Current?.Dispatcher.Invoke(() => AdbProgress = p);
        };

        _detectionService.DevicesUpdated += devices =>
        {
            Application.Current?.Dispatcher.Invoke(() => OnDevicesUpdated(devices));
        };

        _rootService.LogUpdated += msg =>
        {
            Application.Current?.Dispatcher.Invoke(() => AppendLog(msg));
        };

        _rootService.MethodStatusChanged += (type, status) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var method = RootMethods.FirstOrDefault(m => m.Type == type);
                if (method != null) method.Status = status;
            });
        };
    }

    private void InitializeMethods()
    {
        RootMethods.Add(new RootMethod
        {
            Name = "Magisk Patch",
            Description = "Parchea boot.img con Magisk (recomendado)",
            Type = RootMethodType.MagiskPatch,
            Icon = ""
        });
        RootMethods.Add(new RootMethod
        {
            Name = "Desbloquear Bootloader",
            Description = "Desbloquea el bootloader (borra datos)",
            Type = RootMethodType.BootloaderUnlock,
            Icon = ""
        });
        RootMethods.Add(new RootMethod
        {
            Name = "ADB Exploit",
            Description = "Explota vulnerabilidades vía ADB",
            Type = RootMethodType.AdbExploit,
            Icon = ""
        });
        RootMethods.Add(new RootMethod
        {
            Name = "TWRP Recovery",
            Description = "Instala recovery personalizado + Magisk",
            Type = RootMethodType.CustomRecovery,
            Icon = ""
        });
    }

    public async Task InitializeAsync()
    {
        StatusText = "Inicializando ADB...";
        await _adbService.InitializeAsync();
        IsAdbReady = true;
        StatusText = "ADB listo. Conecta un dispositivo Android.";
        _detectionService.Start();
    }

    private void OnDevicesUpdated(List<string> devices)
    {
        DeviceList.Clear();
        foreach (var d in devices)
            DeviceList.Add(d);

        IsDeviceConnected = devices.Count > 0;

        if (devices.Count > 0 && !devices.Contains(SelectedSerial))
        {
            SelectedSerial = devices[0];
        }

        ConnectionStatus = devices.Count > 0
            ? $"Conectado ({devices.Count} dispositivo{(devices.Count > 1 ? "s" : "")})"
            : "Desconectado";

        if (devices.Count > 0)
        {
            StatusText = "Dispositivo detectado. Cargando información...";
            _ = LoadDeviceInfoAsync(SelectedSerial);
        }
        else
        {
            ClearDeviceInfo();
            StatusText = "Esperando dispositivo...";
        }
    }

    private async Task LoadDeviceInfoAsync(string serial)
    {
        if (string.IsNullOrEmpty(serial)) return;

        var info = await _adbService.GetDeviceInfoAsync(serial);
        if (info != null)
        {
            DeviceModel = info.Model;
            DeviceManufacturer = info.Manufacturer;
            DeviceAndroidVersion = info.AndroidVersion;
            DeviceBuild = info.BuildNumber;
            DeviceBattery = info.BatteryLevel;
            DeviceAbi = info.Abi;
            DeviceSecurityPatch = info.SecurityPatch;
            DeviceRam = info.TotalRam > 0 ? $"{info.TotalRam} MB" : "---";
            IsDeviceRooted = info.IsRooted;
            BootloaderUnlocked = info.BootloaderUnlocked;
            AppendLog($"Dispositivo detectado: {info.Manufacturer} {info.Model} (Android {info.AndroidVersion})");
            AppendLog($"Root: {(info.IsRooted ? "✓ CON ROOT" : "✗ Sin root")} | Bootloader: {(info.BootloaderUnlocked ? "Desbloqueado" : "Bloqueado")}");
            StatusText = IsDeviceRooted ? "✓ Dispositivo con root detectado" : "Dispositivo listo para rootear";
        }
    }

    private void ClearDeviceInfo()
    {
        DeviceModel = "---";
        DeviceManufacturer = "---";
        DeviceAndroidVersion = "---";
        DeviceBuild = "---";
        DeviceBattery = "---";
        DeviceAbi = "---";
        DeviceSecurityPatch = "---";
        DeviceRam = "---";
        IsDeviceRooted = false;
        BootloaderUnlocked = false;
    }

    [RelayCommand]
    private async Task SelectDevice(string serial)
    {
        SelectedSerial = serial;
        await LoadDeviceInfoAsync(serial);
    }

    [RelayCommand]
    private async Task ExecuteRootMethod(RootMethod method)
    {
        if (string.IsNullOrEmpty(SelectedSerial))
        {
            AppendLog("[ERROR] No hay dispositivo seleccionado.");
            return;
        }

        if (IsRooting) return;

        IsRooting = true;
        _rootCts = new CancellationTokenSource();

        try
        {
            var status = await _rootService.ExecuteMethodAsync(method, SelectedSerial, _rootCts.Token);
            AppendLog($"Método '{method.Name}' finalizado con estado: {status}");
        }
        finally
        {
            IsRooting = false;
            _rootCts?.Dispose();
            _rootCts = null;
        }
    }

    [RelayCommand]
    private void CancelRoot()
    {
        _rootCts?.Cancel();
        AppendLog("Cancelando operación...");
    }

    [RelayCommand]
    private void RebootDevice()
    {
        if (!string.IsNullOrEmpty(SelectedSerial))
        {
            _adbService.RebootDevice(SelectedSerial);
            AppendLog("Reiniciando dispositivo...");
        }
    }

    [RelayCommand]
    private void RebootBootloader()
    {
        if (!string.IsNullOrEmpty(SelectedSerial))
        {
            _adbService.RebootToBootloader(SelectedSerial);
            AppendLog("Reiniciando a bootloader...");
        }
    }

    [RelayCommand]
    private void RefreshDevice()
    {
        if (!string.IsNullOrEmpty(SelectedSerial))
            _ = LoadDeviceInfoAsync(SelectedSerial);
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogText = string.Empty;
    }

    public void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        _logBuilder.Append(line);
        _logLineCount++;

        if (_logLineCount > MaxLogLines)
        {
            var full = _logBuilder.ToString();
            var idx = full.IndexOf('\n', StringComparison.Ordinal);
            if (idx > 0)
            {
                _logBuilder.Remove(0, idx + 1);
                _logLineCount--;
            }
        }

        LogText = _logBuilder.ToString();
    }

    public void Shutdown()
    {
        _rootCts?.Cancel();
        _rootCts?.Dispose();
        _detectionService.Stop();
        _detectionService.Dispose();
        _adbService.KillAdb();
    }
}
