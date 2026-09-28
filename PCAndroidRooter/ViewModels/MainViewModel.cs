using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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
    private bool _detectionPausedForSamsungUnlock;
    private readonly StringBuilder _logBuilder = new();
    private const int MaxLogLines = 500;
    private int _logLineCount;
    private static readonly string LogDirectory = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "logs");
    private static readonly string LogFilePath = Path.Combine(
        LogDirectory, $"root_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
    private static readonly object LogLock = new();

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

    partial void OnSelectedSerialChanged(string value)
    {
        if (!string.IsNullOrEmpty(value) && IsDeviceConnected)
        {
            _ = LoadDeviceInfoAsync(value);
        }
    }

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

    [ObservableProperty]
    private string _backupDirectory = string.Empty;

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
            Name = "One-Click Root",
            Description = "Intenta automáticamente todos los métodos",
            FriendlyName = " Root Automático",
            FriendlyDescription = "Solo si el bootloader ya está desbloqueado. No borra fotos ni apps. Si está bloqueado, no hace nada.",
            Type = RootMethodType.OneClickRoot,
            Icon = "\uE73A",
            Difficulty = "Media",
            RiskLevel = "Medio"
        });
        RootMethods.Add(new RootMethod
        {
            Name = "Magisk Patch",
            Description = "Parchea boot.img con Magisk (recomendado)",
            FriendlyName = "Root con Magisk",
            FriendlyDescription = "No borra tus datos. Parchea el arranque y guarda una copia del boot original. Requiere bootloader ya desbloqueado.",
            Type = RootMethodType.MagiskPatch,
            Icon = "\uE730",
            Difficulty = "Media",
            RiskLevel = "Medio"
        });
        RootMethods.Add(new RootMethod
        {
            Name = "KernelSU",
            Description = "Alternativa ligera a Magisk",
            FriendlyName = "KernelSU (no disponible)",
            FriendlyDescription = "No está implementado. Antes ejecutaba Magisk con otro nombre.",
            Type = RootMethodType.KernelSU,
            Icon = "\uE730",
            Difficulty = "—",
            RiskLevel = "—",
            Status = RootMethodStatus.NotSupported
        });
        RootMethods.Add(new RootMethod
        {
            Name = "Desbloquear Bootloader",
            Description = "Desbloquea el bootloader (borra datos)",
            FriendlyName = "Desbloquear Bootloader (BORRA TODO)",
            FriendlyDescription = "NO es root. Formatea el teléfono. El backup automático no recupera apps, cuentas ni chats.",
            Type = RootMethodType.BootloaderUnlock,
            Icon = "\uE785",
            Difficulty = "Media",
            RiskLevel = "Alto",
            IsAdvanced = false
        });
        RootMethods.Add(new RootMethod
        {
            Name = "ADB Exploit",
            Description = "Explota vulnerabilidades vía ADB",
            FriendlyName = "Root por ADB (no disponible)",
            FriendlyDescription = "No está implementado. No se intenta y no modifica el teléfono.",
            Type = RootMethodType.AdbExploit,
            Icon = "\uE74C",
            Difficulty = "—",
            RiskLevel = "—",
            IsAdvanced = true,
            Status = RootMethodStatus.NotSupported
        });
        RootMethods.Add(new RootMethod
        {
            Name = "TWRP Recovery",
            Description = "Instala recovery personalizado + Magisk",
            FriendlyName = "Recovery (no disponible)",
            FriendlyDescription = "No instala ningún recovery. Antes solo reiniciaba el teléfono.",
            Type = RootMethodType.CustomRecovery,
            Icon = "\uE74C",
            Difficulty = "—",
            RiskLevel = "—",
            IsAdvanced = true,
            Status = RootMethodStatus.NotSupported
        });
        RootMethods.Add(new RootMethod
        {
            Name = "Root Temporal",
            Description = "No disponible en esta versión",
            FriendlyName = "Root Temporal (no disponible)",
            FriendlyDescription = "No implementado en esta versión. Usa Desbloquear Bootloader + Magisk Patch.",
            Type = RootMethodType.TemporaryRoot,
            Icon = "\uE7BA",
            Difficulty = "Fácil",
            RiskLevel = "Bajo",
            IsAdvanced = true,
            Status = RootMethodStatus.NotSupported
        });
        RootMethods.Add(new RootMethod
        {
            Name = "Fastboot Boot (seguro)",
            Description = "Arranque temporal sin flashear (requiere bootloader desbloqueado)",
            FriendlyName = "Arranque Temporal (seguro)",
            FriendlyDescription = "No modifica el teléfono. Si algo falla, solo reinicia.",
            Type = RootMethodType.FastbootBoot,
            Icon = "\uE7BA",
            Difficulty = "Media",
            RiskLevel = "Bajo",
            IsAdvanced = true
        });
        RootMethods.Add(new RootMethod
        {
            Name = "MTKClient Unlock",
            Description = "Desbloqueo vía bootrom MediaTek (sin toggle OEM)",
            FriendlyName = "Desbloqueo MediaTek (BORRA TODO)",
            FriendlyDescription = "No es root. Formatea el teléfono. El backup automático no guarda tus datos.",
            Type = RootMethodType.MtkClientUnlock,
            Icon = "\uE74C",
            Difficulty = "Avanzado",
            RiskLevel = "Alto",
            IsAdvanced = true
        });

        // Smart recommendation will be set when device is detected
    }

    public async Task InitializeAsync()
    {
        StatusText = "Inicializando ADB...";
        var ok = await _adbService.InitializeAsync();
        if (!ok)
        {
            StatusText = "ERROR: No se pudo inicializar ADB. Revisa la consola.";
            AppendLog("[ERROR FATAL] No se pudo iniciar ADB. Descarga manual: https://developer.android.com/studio/releases/platform-tools");
            return;
        }
        IsAdbReady = true;
        StatusText = "ADB listo. Conecta un dispositivo Android.";
        _detectionService.Start();
    }

    private void OnDevicesUpdated(List<string> devices)
    {
        var previousSerial = SelectedSerial;

        // Reconstruir solo si la membresía cambió — Clear() a secas resetea la
        // selección del ComboBox aunque el mismo serial siga conectado.
        var membershipChanged = devices.Count != DeviceList.Count ||
                                devices.Any(d => !DeviceList.Contains(d));
        if (membershipChanged)
        {
            DeviceList.Clear();
            foreach (var d in devices)
                DeviceList.Add(d);
        }

        IsDeviceConnected = devices.Count > 0;

        if (devices.Count > 0 && !devices.Contains(SelectedSerial))
        {
            // El setter OnSelectedSerialChanged lanza la carga de info (una sola vez)
            SelectedSerial = devices[0];
        }

        ConnectionStatus = devices.Count > 0
            ? $"Conectado ({devices.Count} dispositivo{(devices.Count > 1 ? "s" : "")})"
            : "Desconectado";

        if (devices.Count > 0)
        {
            StatusText = "Dispositivo detectado. Cargando información...";
            // Si la selección cambió arriba, el setter ya cargó; no duplicar la carga.
            if (SelectedSerial == previousSerial)
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

            // Smart recommendation
            UpdateSmartRecommendation(info);
        }
    }

    private void UpdateSmartRecommendation(DeviceInfo info)
    {
        // Clear all recommendations first
        foreach (var method in RootMethods)
            method.IsRecommended = false;

        if (info.IsRooted)
        {
            StatusText = "✓ Este dispositivo ya tiene root. No necesitas hacer nada.";
            return;
        }

        // Bootloader cerrado: rootear implica formatear. No recomendar nada que borre.
        if (!info.BootloaderUnlocked)
        {
            StatusText = "Bootloader bloqueado. No se recomienda rootear: desbloquearlo borraría tus datos.";
            return;
        }

        SetRecommended(RootMethodType.MagiskPatch, "Bootloader desbloqueado. Magisk no borra tus datos.");
    }

    private void SetRecommended(RootMethodType type, string reason)
    {
        var method = RootMethods.FirstOrDefault(m => m.Type == type);
        if (method != null)
        {
            method.IsRecommended = true;
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

        if (!method.IsAvailable || RootSafetyPolicy.IsFakeOrUnsupported(method.Type))
        {
            AppendLog($"'{method.FriendlyName}' no está disponible. No se ha modificado el teléfono.");
            return;
        }

        // El root no desbloquea. Si la UI ve el bootloader cerrado, ni siquiera empieza.
        bool isOneClick = method.Type == RootMethodType.OneClickRoot;
        if ((isOneClick || method.Type == RootMethodType.MagiskPatch || method.Type == RootMethodType.FastbootBoot)
            && !BootloaderUnlocked)
        {
            MessageBox.Show(
                "El bootloader está BLOQUEADO (o no se ha podido confirmar).\n\n" +
                "Rootear este teléfono exigiría desbloquearlo, y eso BORRA todos los datos.\n\n" +
                "No se ha hecho nada. El teléfono sigue igual.",
                "No se toca el teléfono",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            AppendLog("Root cancelado: bootloader bloqueado. No se ha borrado nada.");
            return;
        }

        if (isOneClick)
        {
            var proceed = MessageBox.Show(
                "El bootloader figura como desbloqueado.\n\n" +
                "Se parcheará la partición boot con Magisk. Esto NO borra fotos, apps ni cuentas.\n" +
                "Si el parche falla, el teléfono podría no encender hasta restaurar el boot original (se guarda una copia).\n\n" +
                "¿Continuar?",
                "Root sin borrar datos",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.OK)
            {
                AppendLog("Operación cancelada por el usuario.");
                return;
            }
        }
        else
        {
            var warning = GetRootWarning(method);
            if (!string.IsNullOrEmpty(warning))
            {
                var result = MessageBox.Show(
                    $"{warning}\n\n¿Deseas continuar?",
                    "Aviso",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.OK)
                {
                    AppendLog("Operación cancelada por el usuario.");
                    return;
                }
            }
        }

        IsRooting = true;
        _rootCts = new CancellationTokenSource();
        _detectionService.Pause();
        var keepPausedSamsungUnlock =
            method.Type == RootMethodType.BootloaderUnlock &&
            DeviceManufacturer.Equals("samsung", StringComparison.OrdinalIgnoreCase);

        try
        {
            // Task.Run: saca el flujo de root (bloqueos sync de adb/fastboot) del hilo UI
            var status = await Task.Run(
                () => _rootService.ExecuteMethodAsync(method, SelectedSerial, _rootCts.Token),
                _rootCts.Token);
            AppendLog($"Método '{method.Name}' finalizado con estado: {status}");
        }
        finally
        {
            if (keepPausedSamsungUnlock)
            {
                _detectionService.Pause();
                _detectionPausedForSamsungUnlock = true;
                AppendLog("Detección de dispositivos pausada — sigue las instrucciones para el desbloqueo manual en Download Mode.");
                AppendLog("Cuando termines, pulsa 'Refresh' para reconectar el dispositivo.");
            }
            else
            {
                _detectionService.Resume();
            }
            IsRooting = false;
            _rootCts?.Dispose();
            _rootCts = null;
        }
    }

    [RelayCommand]
    private async Task OneClickRoot()
    {
        if (string.IsNullOrEmpty(SelectedSerial))
        {
            AppendLog("[ERROR] No hay dispositivo conectado.");
            return;
        }

        if (IsRooting) return;

        if (!BootloaderUnlocked)
        {
            MessageBox.Show(
                "El bootloader está BLOQUEADO (o no se ha podido confirmar).\n\n" +
                "Rootear exigiría desbloquearlo, y eso BORRA todos los datos.\n\n" +
                "No se ha hecho nada. El teléfono sigue igual.",
                "No se toca el teléfono",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            AppendLog("Root automático cancelado: bootloader bloqueado. No se ha borrado nada.");
            return;
        }

        var proceed = MessageBox.Show(
            "El bootloader figura como desbloqueado.\n\n" +
            "Se parcheará la partición boot con Magisk. Esto NO borra fotos, apps ni cuentas.\n" +
            "Si el parche falla, el teléfono podría no encender hasta restaurar el boot original (se guarda una copia).\n\n" +
            "¿Continuar?",
            "Root sin borrar datos",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (proceed != MessageBoxResult.OK)
        {
            AppendLog("Operación cancelada por el usuario.");
            return;
        }

        // Find the OneClickRoot method
        var oneClickMethod = RootMethods.FirstOrDefault(m => m.Type == RootMethodType.OneClickRoot);
        if (oneClickMethod == null) return;

        // Execute directly without any prompt
        IsRooting = true;
        _rootCts = new CancellationTokenSource();
        _detectionService.Pause();

        try
        {
            AppendLog("═══════════════════════════════════════════");
            AppendLog("  INICIANDO ROOT AUTOMÁTICO");
            AppendLog("═══════════════════════════════════════════");
            AppendLog("  No desconectes el teléfono.");
            AppendLog("  Esto puede tardar varios minutos...");
            AppendLog("");

            // Task.Run: el flujo de root bloquea con adb/fastboot; no congelar la UI
            var status = await Task.Run(
                () => _rootService.ExecuteMethodAsync(oneClickMethod, SelectedSerial, _rootCts.Token),
                _rootCts.Token);

            if (status == RootMethodStatus.Success)
            {
                AppendLog("");
                AppendLog("═══════════════════════════════════════════");
                AppendLog("  ✅ ¡ROOT COMPLETADO CON ÉXITO!");
                AppendLog("═══════════════════════════════════════════");
            }
            else if (status == RootMethodStatus.WaitingDevice)
            {
                AppendLog("");
                AppendLog("Flujo manual pendiente — sigue las instrucciones de la consola");
                AppendLog("y vuelve a ejecutar 'One-Click Root' cuando el dispositivo esté listo.");
            }
            else
            {
                AppendLog("");
                AppendLog("El root automático no pudo completarse.");
                AppendLog("Revisa la consola para más detalles.");
            }
        }
        finally
        {
            _detectionService.Resume();
            IsRooting = false;
            _rootCts?.Dispose();
            _rootCts = null;
        }
    }

     private static string? GetRootWarning(RootMethod method)
     {
         return method.Type switch
         {
             RootMethodType.BootloaderUnlock =>
                 "ESTO NO ES ROOT. Desbloquear el bootloader BORRA TODO el teléfono.\n\n" +
                 "El backup automático es parcial (unos APK sin sus datos, y pocas fotos). " +
                 "NO recupera cuentas, chats, contraseñas ni la mayoría de archivos.\n\n" +
                 "Si quieres conservar los datos, pulsa Cancelar.",
             RootMethodType.MagiskPatch =>
                 "Se modifica el arranque del teléfono. NO se borran tus datos.\n\n" +
                 "Si algo sale mal, el teléfono podría no encender hasta restaurar el boot original, que se guarda antes.",
             RootMethodType.AdbExploit =>
                 "Este método no está implementado. No se modificará el teléfono.",
             RootMethodType.CustomRecovery =>
                 "Este método no instala un recovery. No se modificará el teléfono.",
             RootMethodType.KernelSU =>
                 "KernelSU no está implementado. No se modificará el teléfono.",
             RootMethodType.OneClickRoot =>
                 "Solo parchea el boot con Magisk si el bootloader ya está desbloqueado. " +
                 "No borra tus datos. No desbloquea el bootloader.",
            RootMethodType.TemporaryRoot =>
                "Root temporal NO disponible en esta versión. " +
                "Usa 'Desbloquear Bootloader' + 'Magisk Patch' en su lugar.",
         RootMethodType.FastbootBoot =>
                     "Arrancará el teléfono temporalmente con root. " +
                     "NO modifica el teléfono permanentemente. " +
                     "Si algo falla, solo reinicia el teléfono.",
                RootMethodType.MtkClientUnlock =>
                    "ESTO NO ES ROOT. El desbloqueo MediaTek BORRA TODO el teléfono.\n\n" +
                    "El backup automático no guarda tus datos. Knox, si aplica, se dispara para siempre.\n\n" +
                    "Si quieres conservar los datos, pulsa Cancelar.",
                _ => null
         };
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
            var result = MessageBox.Show(
                "¿Reiniciar el dispositivo? Se cerrarán todas las apps en ejecución.",
                "Confirmar reinicio",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (result == MessageBoxResult.OK)
            {
                _adbService.RebootDevice(SelectedSerial);
                AppendLog("Reiniciando dispositivo...");
            }
        }
    }

    [RelayCommand]
    private void RebootBootloader()
    {
        if (!string.IsNullOrEmpty(SelectedSerial))
        {
            var result = MessageBox.Show(
                "¿Reiniciar a modo bootloader/fastboot?\n\n" +
                "El dispositivo no arrancará Android hasta que se reinicie normalmente.",
                "Confirmar reinicio a bootloader",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (result == MessageBoxResult.OK)
            {
                _adbService.RebootToBootloader(SelectedSerial);
                AppendLog("Reiniciando a bootloader...");
            }
        }
    }

    [RelayCommand]
    private async Task RestartAdbAsync()
    {
        AppendLog("Reiniciando servidor ADB...");
        // Asíncrono: la versión síncrona hace Task.Delay(500).Wait() y congela la UI
        await _adbService.RestartAdbAsync();
        IsAdbReady = true;
        AppendLog("ADB reiniciado.");
        _ = LoadDeviceInfoAsync(SelectedSerial);
    }

    [RelayCommand]
    private void RefreshDevice()
    {
        // Reanudar detección si quedó pausada por el flujo manual de Samsung unlock
        if (_detectionPausedForSamsungUnlock)
        {
            _detectionPausedForSamsungUnlock = false;
            _detectionService.Resume();
            AppendLog("Detección de dispositivos reanudada.");
        }
        if (!string.IsNullOrEmpty(SelectedSerial))
            _ = LoadDeviceInfoAsync(SelectedSerial);
    }

    [RelayCommand]
    private void ClearLog()
    {
        lock (_logLock)
        {
            _logBuilder.Clear();
            _logLineCount = 0;
            LogText = string.Empty;
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            System.Diagnostics.Process.Start("explorer.exe", LogDirectory);
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] No se pudo abrir la carpeta de logs: {ex.Message}");
        }
    }

[RelayCommand]
     private async Task RestoreBackup()
     {
         if (string.IsNullOrEmpty(SelectedSerial))
         {
             AppendLog("[ERROR] No hay dispositivo seleccionado.");
             return;
         }

         if (string.IsNullOrEmpty(BackupDirectory) || !Directory.Exists(BackupDirectory))
         {
             AppendLog("[ERROR] El directorio de backup no existe o está vacío.");
             return;
         }

         if (IsRooting)
         {
             AppendLog("[ERROR] Ya hay una operación en curso.");
             return;
         }

         IsRooting = true;
         _rootCts = new CancellationTokenSource();
         _detectionService.Pause();

         try
         {
             AppendLog($"Iniciando restauración desde: {BackupDirectory}");
             var backupDir = BackupDirectory;
             var serial = SelectedSerial;
             var token = _rootCts.Token;
             // Task.Run: RestoreBackupAsync hace adb push/install sincrónicos
              await Task.Run(async () =>
              {
                  await foreach (var status in _rootService.RestoreBackupAsync(serial, backupDir, token))
                  {
                      AppendLog(status);
                  }
              }, token);
          }
          catch (Exception ex)
          {
              AppendLog($"[ERROR] Error durante restauración: {ex.Message}");
          }
          finally
          {
              _detectionService.Resume();
              IsRooting = false;
              _rootCts?.Dispose();
              _rootCts = null;
          }
      }

     [RelayCommand]
     private void BrowseBackupDirectory()
     {
         var dialog = new Microsoft.Win32.OpenFileDialog
         {
             Title = "Seleccionar archivo de backup ZIP o carpeta",
             Filter = "Archivos ZIP de backup|*.zip|Todos los archivos|*.*",
             CheckFileExists = true,
             Multiselect = false
         };

         if (dialog.ShowDialog() == true)
         {
             var selectedPath = dialog.FileName;
             if (File.Exists(selectedPath) && selectedPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
             {
                 // Auto-extract ZIP
                 var backupDir = Path.Combine(Path.GetDirectoryName(selectedPath) ?? "", Path.GetFileNameWithoutExtension(selectedPath));
                 try
                 {
                     if (Directory.Exists(backupDir))
                     {
                         AppendLog($"Directorio ya existe: {backupDir}");
                     }
                     else
                     {
                         AppendLog($"Descomprimiendo backup: {Path.GetFileName(selectedPath)}...");
                         System.IO.Compression.ZipFile.ExtractToDirectory(selectedPath, backupDir);
                         AppendLog($"Backup descomprimido en: {backupDir}");
                     }
                     BackupDirectory = backupDir;
                 }
                 catch (Exception ex)
                 {
                     AppendLog($"[ERROR] No se pudo descomprimir: {ex.Message}");
                 }
             }
             else if (Directory.Exists(selectedPath))
             {
                 BackupDirectory = selectedPath;
                 AppendLog($"Directorio de backup seleccionado: {selectedPath}");
             }
             else
             {
                 AppendLog("[ERROR] Seleccione un archivo ZIP válido o una carpeta de backup.");
             }
         }
     }

    private readonly object _logLock = new();

    public void AppendLog(string message)
    {
        lock (_logLock)
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
        AppendToFile(message);
    }

    private static void AppendToFile(string message)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogFilePath,
                    $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // No romper la app si falla escribir log
        }
    }

    public void Shutdown()
    {
        // Cancelar la operación en curso; NO Dispose mientras esté en vuelo
        // (Dispose invalida el token → ObjectDisposedException en el proceso activo).
        // El proceso va a salir; el GC se encarga del CTS.
        _rootCts?.Cancel();
        _detectionService.Stop();
        _detectionService.Dispose();
        _adbService.Dispose();
        AppendToFile("=== APLICACIÓN CERRADA ===");
    }
}
