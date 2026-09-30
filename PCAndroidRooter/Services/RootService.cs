using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCAndroidRooter.Models;

namespace PCAndroidRooter.Services;

public class RootService
{
    private readonly AdbService _adbService;
    private readonly MagiskService _magiskService;
    private readonly StringBuilder _logOutput = new();
    private readonly object _logLock = new();

    public event Action<string>? LogUpdated;
    public event Action<RootMethodType, RootMethodStatus>? MethodStatusChanged;

    public string LogOutput
    {
        get { lock (_logLock) { return _logOutput.ToString(); } }
    }

    public RootService(AdbService adbService, MagiskService magiskService)
    {
        _adbService = adbService;
        _magiskService = magiskService;
    }

    public async Task<RootMethodStatus> ExecuteMethodAsync(RootMethod method, string serial, CancellationToken ct)
    {
        if (RootSafetyPolicy.IsFakeOrUnsupported(method.Type))
        {
            Log($"'{method.Name}' no está implementado.");
            Log("  No se ha reiniciado ni modificado el teléfono.");
            Log("  Para rootear sin borrar datos: bootloader ya desbloqueado + Magisk Patch.");
            MethodStatusChanged?.Invoke(method.Type, RootMethodStatus.NotSupported);
            return RootMethodStatus.NotSupported;
        }

        MethodStatusChanged?.Invoke(method.Type, RootMethodStatus.Running);

        // Verificar que ADB responde antes de empezar
        if (!_adbService.CheckAdbHealthy())
        {
            var diagnosis = _adbService.DiagnoseAdbIssue();
            Log($"ERROR: {diagnosis ?? "ADB no responde."}");
            Log("  Prueba a reiniciar ADB o el PC.");
            MethodStatusChanged?.Invoke(method.Type, RootMethodStatus.Failed);
            return RootMethodStatus.Failed;
        }

        try
        {
            RootMethodStatus status;
            switch (method.Type)
            {
                case RootMethodType.MagiskPatch:
                    status = await MagiskRootAsync(serial, ct);
                    break;
                case RootMethodType.BootloaderUnlock:
                    status = await UnlockBootloaderAsync(serial, ct);
                    break;
                case RootMethodType.AdbExploit:
                    status = await AdbExploitRootAsync(serial, ct);
                    break;
case RootMethodType.CustomRecovery:
                     status = await CustomRecoveryRootAsync(serial, ct);
                     break;
                 case RootMethodType.KernelSU:
                     status = await KernelSURootAsync(serial, ct);
                     break;
                 case RootMethodType.OneClickRoot:
                     status = await OneClickRootAsync(serial, ct);
                     break;
                 case RootMethodType.TemporaryRoot:
                     status = await TemporaryRootAsync(serial, ct);
                     break;
                case RootMethodType.FastbootBoot:
                    status = await FastbootBootAsync(serial, ct);
                    break;
                case RootMethodType.MtkClientUnlock:
                    status = await MtkClientUnlockAsync(serial, ct);
                    break;
                default:
                    status = RootMethodStatus.NotSupported;
                    break;
            }

            MethodStatusChanged?.Invoke(method.Type, status);
            return status;
        }
        catch (OperationCanceledException)
        {
            Log("Operación cancelada por el usuario.");
            CleanupDeviceTempFiles(serial);
            MethodStatusChanged?.Invoke(method.Type, RootMethodStatus.Failed);
            return RootMethodStatus.Failed;
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            CleanupDeviceTempFiles(serial);
            MethodStatusChanged?.Invoke(method.Type, RootMethodStatus.Failed);
            return RootMethodStatus.Failed;
        }
    }

    private void CleanupDeviceTempFiles(string serial)
    {
        try
        {
            _adbService.Shell(serial, "rm -rf /data/local/tmp/magisk /data/local/tmp/boot_to_patch.img /data/local/tmp/ramdisk.cpio /data/local/tmp/new-boot.img /data/local/tmp/boot_to_patch.img.bak /data/local/tmp/boot_to_patch.img_tmp");
        }
        catch { }
    }

    private const string RemoteMagiskDir = "/data/local/tmp/magisk";
    private static readonly string[] MagiskBinaryFiles =
        { "magiskboot", "magiskboot32", "magisk64", "magisk32", "magiskinit" };

    private int PushMagiskBinaries(string serial, string deviceAbi, CancellationToken ct)
    {
        _adbService.Shell(serial, $"mkdir -p {RemoteMagiskDir}", ct: ct);
        _adbService.Shell(serial, $"rm -rf {RemoteMagiskDir}/*", ct: ct);

        // Incluir copias específicas de la ABI del dispositivo
        var files = new List<string>(MagiskBinaryFiles)
        {
            $"magiskboot-{deviceAbi}",
            $"magiskinit-{deviceAbi}",
            $"magisk64-{deviceAbi}",
            $"magisk32-{deviceAbi}"
        };

        var pushed = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var localPath = Path.Combine(_magiskService.MagiskDir, file);
            if (!File.Exists(localPath)) continue;
            var result = _adbService.PushFile(serial, localPath, $"{RemoteMagiskDir}/{file}");
            if (result.Success)
            {
                pushed++;
                _adbService.Shell(serial, $"chmod 755 {RemoteMagiskDir}/{file}", ct: ct);
            }
        }
        return pushed;
    }

    private string RemoteMagiskBootPath(string deviceAbi)
    {
        var dir = _magiskService.MagiskDir;
        var is64 = deviceAbi.Contains("64", StringComparison.OrdinalIgnoreCase) ||
                   deviceAbi.Contains("arm64", StringComparison.OrdinalIgnoreCase);

        // 1) Copia específica de la ABI del dispositivo
        if (File.Exists(Path.Combine(dir, $"magiskboot-{deviceAbi}")))
            return $"{RemoteMagiskDir}/magiskboot-{deviceAbi}";

        // 2) 32-bit → magiskboot32 (el genérico 'magiskboot' es arm64 por defecto)
        if (!is64 && File.Exists(Path.Combine(dir, "magiskboot32")))
            return $"{RemoteMagiskDir}/magiskboot32";

        if (File.Exists(Path.Combine(dir, "magiskboot")))
            return $"{RemoteMagiskDir}/magiskboot";

        return $"{RemoteMagiskDir}/magiskboot32";
    }

    private string LocalMagiskBinPath(string remoteName, string deviceAbi)
    {
        // Preferir la copia específica de ABI si existe en local
        if (File.Exists(Path.Combine(_magiskService.MagiskDir, $"{remoteName}-{deviceAbi}")))
            return $"{remoteName}-{deviceAbi}";
        return remoteName;
    }

    private async Task<RootMethodStatus> MagiskRootAsync(string serial, CancellationToken ct)
    {
        Log("╔═══════════════════════════════════════════╗");
        Log("║   ROOT VÍA MAGISK                        ║");
        Log("╚═══════════════════════════════════════════╝");

        // ════════════════════════════════════════════════════
        // PASO 1: Verificar dispositivo y estado
        // ════════════════════════════════════════════════════
        Log("\nPASO 1: Verificando dispositivo...");
        var deviceInfo = await _adbService.GetDeviceInfoAsync(serial);
        if (deviceInfo == null)
        {
            LogError("No se pudo obtener información del dispositivo.");
            Log("  Asegúrate de que:");
            Log("  1. El dispositivo está conectado por USB");
            Log("  2. Depuración USB está activada");
            Log("  3. Aceptaste la solicitud RSA en la pantalla");
            return RootMethodStatus.Failed;
        }

        LogOk($"Dispositivo: {deviceInfo.Manufacturer} {deviceInfo.Model}");
        LogOk($"Android: {deviceInfo.AndroidVersion} | ABI: {deviceInfo.Abi}");
        LogOk($"Batería: {deviceInfo.BatteryLevel}");

        // Verificar batería mínima (fail-closed: sin lectura fiable no se procede)
        if (!await EnsureMinBatteryAsync(serial, deviceInfo))
            return RootMethodStatus.Failed;

        // Verificar que no esté en recovery/fastboot
        if (deviceInfo.IsRecovery)
        {
            LogWarning("El dispositivo está en modo Recovery.");
            Log("  Reiniciando a Android normal...");
            _adbService.RebootDevice(serial);
            await Task.Delay(5000, ct);
            _adbService.RestartAdb();
            await Task.Delay(3000, ct);
        }
        ct.ThrowIfCancellationRequested();

        var unlockGate = await EnsureBootloaderForRootAsync(serial, ct);
        if (unlockGate != null)
            return unlockGate.Value;

        // ════════════════════════════════════════════════════
        // PASO 2: Detectar partición boot (con soporte A/B)
        // ════════════════════════════════════════════════════
        Log("\nPASO 2: Detectando partición boot...");
        var bootPart = await _adbService.FindBootPartitionAsync(serial, ct);
        if (bootPart == null)
        {
            LogError("No se encontró init_boot ni boot.");
            Log("  Posibles causas:");
            Log("  • La partición no es legible sin root. Hace falta el archivo oficial de esta misma versión.");
            Log("  • Particiones con otro nombre.");
            Log("  No se desbloquea el bootloader desde aquí: eso borraría los datos.");
            return RootMethodStatus.Failed;
        }
        LogOk($"Partición boot: {bootPart.Path} ({bootPart.Size / 1024 / 1024} MB)");

        // Detectar slot A/B si aplica
        var activeSlot = BootImageValidator.DetectActiveBootSlot(serial, cmd =>
        {
            var r = _adbService.Shell(serial, cmd);
            return (r.Success, r.Output);
        });
        if (activeSlot != null)
        {
            LogOk($"Sistema A/B detectado. Slot activo: {activeSlot}");
            // Reemplazar el sufijo _a/_b existente (antes concatenaba → boot_a_b inexistente).
            var basePath = bootPart.Path;
            if (basePath.EndsWith("_a", StringComparison.Ordinal) || basePath.EndsWith("_b", StringComparison.Ordinal))
                basePath = basePath[..^2];
            if (!basePath.EndsWith(activeSlot, StringComparison.Ordinal))
            {
                var slotPath = basePath + activeSlot;
                var slotCheck = _adbService.Shell(serial, $"ls -l {slotPath} 2>/dev/null");
                if (slotCheck.Success && !slotCheck.Output.Contains("No such file"))
                {
                    LogOk($"Usando partición del slot activo: {slotPath}");
                    bootPart.Path = slotPath;
                    bootPart.BlockDevice = slotPath;
                }
                else
                {
                    LogWarning($"Partición del slot activo no encontrada ({slotPath}); se mantiene: {bootPart.Path}");
                }
            }
        }

        bootPart.FastbootPartition = RootSafetyPolicy.FastbootNameFromBlockPath(
            string.IsNullOrEmpty(bootPart.FastbootPartition) ? bootPart.Path : bootPart.FastbootPartition);
        if (bootPart.Path.Contains(bootPart.FastbootPartition, StringComparison.Ordinal) == false)
            bootPart.FastbootPartition = RootSafetyPolicy.FastbootNameFromBlockPath(bootPart.Path);
        if (!RootSafetyPolicy.IsFlashableBootPartition(bootPart.FastbootPartition))
        {
            LogError($"Partición no permitida ({bootPart.FastbootPartition}). Solo boot o init_boot.");
            Log("  No se ha reiniciado el teléfono.");
            return RootMethodStatus.Failed;
        }
        LogOk($"Partición fastboot: {bootPart.FastbootPartition}" +
              (RootSafetyPolicy.IsInitBootPartition(bootPart.FastbootPartition) ? " (init_boot, Android 13+)" : ""));
        ct.ThrowIfCancellationRequested();

        // ════════════════════════════════════════════════════
        // PASO 3: Verificar e instalar binaries Magisk
        // ════════════════════════════════════════════════════
        Log("\nPASO 3: Verificando binaries de Magisk...");
        if (!_magiskService.HasBinaries)
        {
            Log("  Binaries no encontrados. Descargando Magisk...");
            var downloaded = await _magiskService.DownloadMagiskAsync();
            if (!downloaded)
            {
                LogError("No se pudo descargar Magisk.");
                Log("  Verifica tu conexión a internet.");
                return RootMethodStatus.Failed;
            }
        }

        // Verificar integridad de los binaries críticos (preferir copia de la ABI del dispositivo)
        var abiBootPath = Path.Combine(_magiskService.MagiskDir, $"magiskboot-{deviceInfo.Abi}");
        var magiskBootPath = Path.Combine(_magiskService.MagiskDir, "magiskboot");
        var magiskBoot32Path = Path.Combine(_magiskService.MagiskDir, "magiskboot32");
        var magiskInitPath = Path.Combine(_magiskService.MagiskDir, "magiskinit");

        var is64Device = deviceInfo.Abi.Contains("64", StringComparison.OrdinalIgnoreCase) ||
                          deviceInfo.Abi.Contains("arm64", StringComparison.OrdinalIgnoreCase);
        if (!File.Exists(abiBootPath) && !File.Exists(magiskBootPath) && !File.Exists(magiskBoot32Path))
        {
            LogError("No se encontró magiskboot ni magiskboot32.");
            Log("  Los binaries de Magisk están corruptos o incompletos.");
            return RootMethodStatus.Failed;
        }

        string activeMagiskBoot;
        if (File.Exists(abiBootPath))
            activeMagiskBoot = abiBootPath;
        else if (is64Device && File.Exists(magiskBootPath))
            activeMagiskBoot = magiskBootPath;
        else if (!is64Device && File.Exists(magiskBoot32Path))
            activeMagiskBoot = magiskBoot32Path;
        else
            activeMagiskBoot = File.Exists(magiskBootPath) ? magiskBootPath : magiskBoot32Path;
        var magiskBootInfo = new FileInfo(activeMagiskBoot);
        if (magiskBootInfo.Length < 1024) // Menos de 1 KB es sospechoso
        {
            LogError($"magiskboot es sospechosamente pequeño ({magiskBootInfo.Length} bytes).");
            Log("  Binary corrupto. Intenta descargar Magisk de nuevo.");
            return RootMethodStatus.Failed;
        }

        var version = await _magiskService.GetLatestVersionAsync();
        LogOk($"Magisk {version ?? "desconocido"} verificado ({magiskBootInfo.Length / 1024} KB).");
        ct.ThrowIfCancellationRequested();

        // ════════════════════════════════════════════════════
        // PASO 4: Extraer boot.img del dispositivo
        // ════════════════════════════════════════════════════
        Log("\nPASO 4: Extrayendo boot.img del dispositivo...");
        var bootImgLocal = Path.Combine(_magiskService.MagiskDir, "boot.img");
            var bootImgBackup = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            $"boot_original_{AdbService.SanitizeFileName(deviceInfo.Model)}_{DateTime.Now:yyyyMMdd_HHmmss}.img");

        var extracted = await _adbService.ExtractBootImgAsync(serial, bootPart, bootImgLocal, ct);
        if (!extracted)
        {
            LogError("No se pudo extraer boot.img.");
            Log("  Causas posibles:");
            Log("  • Bootloader bloqueado");
            Log("  • Partición boot no accesible");
            Log("  • Permisos insuficientes (prueba con 'adb root')");
            return RootMethodStatus.Failed;
        }

        // ── VALIDACIÓN CRÍTICA: boot.img original ──
        Log("  Validando boot.img original...");
        var originalValidation = BootImageValidator.ValidateOriginal(bootImgLocal);
        if (originalValidation.Status != BootImageValidator.ValidationStatus.Valid)
        {
            LogError($"boot.img original INVÁLIDO: {originalValidation.Message}");
            Log("  No se continuará con un archivo corrupto.");
            File.Delete(bootImgLocal);
            return RootMethodStatus.Failed;
        }
        LogOk($"boot.img original: VÁLIDO ({originalValidation.FileSize / 1024 / 1024} MB)");
        LogOk($"  SHA256: {originalValidation.Sha256?[..16]}...");

        // Guardar backup del original
        try
        {
            File.Copy(bootImgLocal, bootImgBackup, overwrite: true);
            LogOk($"  Backup del boot original guardado en: {bootImgBackup}");
        }
        catch (Exception ex)
        {
            LogError($"No se pudo guardar la copia original: {ex.Message}");
            Log("  Sin esa copia no hay marcha atrás. No se parchea ni se flashea.");
            return RootMethodStatus.Failed;
        }
        if (!File.Exists(bootImgBackup))
        {
            LogError("La copia original no quedó en disco. No se continúa.");
            return RootMethodStatus.Failed;
        }
        ct.ThrowIfCancellationRequested();

        // ════════════════════════════════════════════════════
        // PASO 5: Subir binaries Magisk al dispositivo
        // ════════════════════════════════════════════════════
        Log("\nPASO 5: Preparando binaries de Magisk en el dispositivo...");
        var remoteDir = RemoteMagiskDir;
        var pushed = PushMagiskBinaries(serial, deviceInfo.Abi, ct);

        if (pushed == 0)
        {
            LogError("No se pudieron subir los binaries de Magisk al dispositivo.");
            Log("  Verifica que la depuración USB esté activa y autorizada.");
            return RootMethodStatus.Failed;
        }
        LogOk($"  {pushed} binaries subidos y permisos configurados.");

        // Ruta remota del magiskboot correcto para la ABI del dispositivo
        var magiskBootBin = RemoteMagiskBootPath(deviceInfo.Abi);

        // Verificar que magiskboot se ejecuta en el dispositivo
        var magiskBootTest = _adbService.Shell(serial, $"{magiskBootBin} --help 2>&1 | head -1");
        if (!magiskBootTest.Success || string.IsNullOrWhiteSpace(magiskBootTest.Output))
        {
            // Intentar con magiskboot32
            magiskBootTest = _adbService.Shell(serial, $"{remoteDir}/magiskboot32 --help 2>&1 | head -1");
        }
        if (!magiskBootTest.Success || string.IsNullOrWhiteSpace(magiskBootTest.Output))
        {
            LogError("magiskboot no se ejecuta en el dispositivo.");
            Log("  El binary puede ser incompatible con la ABI del dispositivo.");
            Log($"  ABI del dispositivo: {deviceInfo.Abi}");
            Log($"  Binario probado: {magiskBootBin}");
            Log("  Prueba a descargar Magisk manualmente desde:");
            Log("  https://github.com/topjohnwu/Magisk/releases");
            return RootMethodStatus.Failed;
        }
        LogOk("  magiskboot ejecuta correctamente en el dispositivo.");
        ct.ThrowIfCancellationRequested();

        // ════════════════════════════════════════════════════
        // PASO 6: Subir boot.img al dispositivo
        // ════════════════════════════════════════════════════
        Log("\nPASO 6: Subiendo boot.img al dispositivo...");
        var remoteBoot = "/data/local/tmp/boot_to_patch.img";
        var pushResult = _adbService.PushFile(serial, bootImgLocal, remoteBoot);
        if (!pushResult.Success)
        {
            LogError("No se pudo subir boot.img al dispositivo.");
            return RootMethodStatus.Failed;
        }
        LogOk($"  boot.img subido a {remoteBoot}");
        ct.ThrowIfCancellationRequested();

        // ════════════════════════════════════════════════════
        // PASO 7: Parchear boot.img con Magisk
        // ════════════════════════════════════════════════════
        Log("\nPASO 7: Parcheando boot.img con Magisk...");

        // Desempaquetar boot.img
        Log("  [7.1] Desempaquetando boot.img...");
        var cleanupCmd = "rm -rf /data/local/tmp/magisk /data/local/tmp/boot_to_patch.img /data/local/tmp/ramdisk.cpio /data/local/tmp/new-boot.img /data/local/tmp/boot_to_patch.img.bak /data/local/tmp/boot_to_patch.img_tmp";
        // SIEMPRE limpiar antes del unpack: elimina new-boot.img stale de runs anteriores
        // (si no, un repack fallido podría leer la imagen vieja y flashear basura → brick).
        _adbService.Shell(serial, cleanupCmd, timeoutMs: 15000, ct: ct);
        var unpackResult = _adbService.Shell(serial, $"cd /data/local/tmp && {magiskBootBin} unpack boot_to_patch.img 2>&1", timeoutMs: 60000, ct: ct);
        if (!unpackResult.Success)
        {
            Log("  Error en desempaquete, intentando método alternativo...");
            unpackResult = _adbService.Shell(serial,
                $"cd /data/local/tmp && {magiskBootBin} unpack -h boot_to_patch.img 2>&1", timeoutMs: 60000, ct: ct);
            if (!unpackResult.Success)
            {
                LogError("No se pudo desempaquetar boot.img.");
                Log("  El boot.img puede estar corrupto o ser incompatible.");
                Log("  El archivo original está respaldado en: " + bootImgBackup);
                return RootMethodStatus.Failed;
            }
        }
        LogOk("  boot.img desempaquetado.");

        // Verificar que se generaron los archivos necesarios
        var ramdiskCheck = _adbService.Shell(serial, "ls -la /data/local/tmp/ramdisk.cpio 2>/dev/null", ct: ct);
        if (!ramdiskCheck.Success || ramdiskCheck.Output.Contains("No such file"))
        {
            LogError("No se generó ramdisk.cpio durante el desempaquete.");
            Log("  El boot.img puede no tener ramdisk (dispositivo con init_in_boot).");
            Log("  Este dispositivo puede requerir un método diferente.");
            _adbService.Shell(serial, cleanupCmd, ct: ct);
            return RootMethodStatus.Failed;
        }
        LogOk("  ramdisk.cpio verificado.");
        ct.ThrowIfCancellationRequested();

        // Inyectar Magisk en ramdisk (usar copia de la ABI del dispositivo si existe)
        Log("  [7.2] Inyectando Magisk en ramdisk...");
        var magiskBinName = is64Device ? "magisk64" : "magisk32";
        var magiskBinPath = $"{remoteDir}/{LocalMagiskBinPath(magiskBinName, deviceInfo.Abi)}";
        var magiskInitPathRemote = $"{remoteDir}/{LocalMagiskBinPath("magiskinit", deviceInfo.Abi)}";

        // Método 1: Inyección completa via overlay.d
        var patchCmd = $"cd /data/local/tmp && " +
            $"{magiskBootBin} cpio ramdisk.cpio 'mkdir 0750 overlay.d' && " +
            $"{magiskBootBin} cpio ramdisk.cpio 'mkdir 0750 overlay.d/sbin' && " +
            $"{magiskBootBin} cpio ramdisk.cpio 'add 0750 overlay.d/sbin/magisk {magiskBinPath}' && " +
            $"{magiskBootBin} cpio ramdisk.cpio 'add 0644 overlay.d/sbin/magiskinit {magiskInitPathRemote}' 2>&1";

        var patchResult = _adbService.Shell(serial, patchCmd, timeoutMs: 120000, ct: ct);
        if (!patchResult.Success || patchResult.Error.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            Log("  Inyección via overlay.d falló, intentando método directo...");

            // Método 2: Inyección directa en sbin
            var simplePatch = $"cd /data/local/tmp && " +
                $"{magiskBootBin} cpio ramdisk.cpio 'add 0750 sbin/magisk {magiskBinPath}' 2>&1";
            patchResult = _adbService.Shell(serial, simplePatch, timeoutMs: 120000, ct: ct);

            if (!patchResult.Success || patchResult.Error.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                LogError("No se pudo inyectar Magisk en el ramdisk.");
                Log("  Restaurando estado limpio...");
                CleanupDeviceTempFiles(serial);
                Log("  El archivo original está respaldado en: " + bootImgBackup);
                return RootMethodStatus.Failed;
            }
        }

        // Verificar que magisk fue inyectado (extracción positiva = ground truth)
        var injectionVerified = false;
        long injectedSize = 0;
        _adbService.Shell(serial, "rm -f /data/local/tmp/_verify_magisk", ct: ct);
        foreach (var entry in new[] { "overlay.d/sbin/magisk", "sbin/magisk" })
        {
            _adbService.Shell(serial,
                $"cd /data/local/tmp && {magiskBootBin} cpio ramdisk.cpio 'extract {entry} _verify_magisk' 2>&1", ct: ct);
            var vSize = _adbService.Shell(serial, "wc -c < /data/local/tmp/_verify_magisk 2>/dev/null", ct: ct);
            if (long.TryParse(vSize.Output.Trim(), out injectedSize) && injectedSize > 10000)
            {
                injectionVerified = true;
                break;
            }
        }
        _adbService.Shell(serial, "rm -f /data/local/tmp/_verify_magisk", ct: ct);

        if (injectionVerified)
        {
            LogOk($"  Magisk inyectado y verificado en ramdisk ({injectedSize} bytes).");
        }
        else
        {
            // Fallback: comprobar 'exists' de magiskboot
            var verifyPatch = _adbService.Shell(serial,
                $"{magiskBootBin} cpio ramdisk.cpio 'exists overlay.d/sbin/magisk' 2>&1", ct: ct);
            var verifyPatch2 = _adbService.Shell(serial,
                $"{magiskBootBin} cpio ramdisk.cpio 'exists sbin/magisk' 2>&1", ct: ct);
            injectionVerified =
                (verifyPatch.Success && (verifyPatch.Output.Contains("1") || verifyPatch.Output.Contains("true"))) ||
                (verifyPatch2.Success && (verifyPatch2.Output.Contains("1") || verifyPatch2.Output.Contains("true")));

            if (injectionVerified)
                LogOk("  Magisk inyectado en ramdisk (verificado vía exists).");
            else
                LogWarning("  No se pudo verificar la inyección de Magisk en el ramdisk.");
        }
        ct.ThrowIfCancellationRequested();

        // Reempaquetar boot.img
        Log("  [7.3] Reempaquetando boot.img...");
        var repackCmd = $"cd /data/local/tmp && {magiskBootBin} repack boot_to_patch.img 2>&1";
        var repackResult = _adbService.Shell(serial, repackCmd, timeoutMs: 120000, ct: ct);

        if (!repackResult.Success)
        {
            repackResult = _adbService.Shell(serial,
                $"cd /data/local/tmp && {magiskBootBin} repack -n boot_to_patch.img new-boot.img 2>&1", timeoutMs: 120000, ct: ct);
        }

        // Verificar que el reempaquetado generó un archivo válido
        var repackCheck = _adbService.Shell(serial, "wc -c < /data/local/tmp/new-boot.img 2>/dev/null", ct: ct);
        var repackedSize = 0L;
        long.TryParse(repackCheck.Output.Trim(), out repackedSize);
        if (repackedSize < BootImageValidator.BootImageMinSize)
        {
            LogError($"boot.img reempaquetado inválido ({repackedSize} bytes).");
            Log("  El reempaquete falló. No se flasheará un archivo corrupto.");
            _adbService.Shell(serial, "rm -rf /data/local/tmp/magisk /data/local/tmp/boot_to_patch.img /data/local/tmp/ramdisk.cpio /data/local/tmp/new-boot.img /data/local/tmp/boot_to_patch.img.bak");
            Log("  El archivo original está respaldado en: " + bootImgBackup);
            return RootMethodStatus.Failed;
        }
        LogOk($"  boot.img reempaquetado: {repackedSize / 1024 / 1024} MB");
        ct.ThrowIfCancellationRequested();

        // ════════════════════════════════════════════════════
        // PASO 8: Descargar boot.img parcheado al PC
        // ════════════════════════════════════════════════════
        Log("\nPASO 8: Descargando boot.img parcheado...");
        var patchedImgLocal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            $"magisk_patched_{AdbService.SanitizeFileName(deviceInfo.Model)}_{DateTime.Now:yyyyMMdd_HHmmss}.img");

        // Solo new-boot.img: boot_to_patch.img es el ORIGINAL SIN PARCHAR —
        // usarlo como fallback arriesga flashear/restaurar un boot sin Magisk.
        var remotePatchedPaths = new[] { "/data/local/tmp/new-boot.img" };
        bool pulled = false;
        foreach (var remotePath in remotePatchedPaths)
        {
            var result = _adbService.PullFile(serial, remotePath, patchedImgLocal);
            if (result.Success && File.Exists(patchedImgLocal) &&
                new FileInfo(patchedImgLocal).Length > BootImageValidator.BootImageMinSize)
            {
                pulled = true;
                break;
            }
        }

        if (!pulled)
        {
            Log("  Pull normal falló, usando exec-out (binario directo)...");
            pulled = await _adbService.ExecuteAdbRawAsync(serial, remotePatchedPaths[0], patchedImgLocal, ct);
        }

        if (!pulled)
        {
            LogError("No se pudo descargar el boot.img parcheado.");
            _adbService.Shell(serial, "rm -rf /data/local/tmp/magisk /data/local/tmp/boot_to_patch.img /data/local/tmp/ramdisk.cpio /data/local/tmp/new-boot.img /data/local/tmp/boot_to_patch.img.bak");
            return RootMethodStatus.Failed;
        }
        ct.ThrowIfCancellationRequested();

        // ════════════════════════════════════════════════════
        // PASO 9: VALIDACIÓN DE SEGURIDAD DEL BOOT.PARCHADO
        // ════════════════════════════════════════════════════
        Log("\nPASO 9: Validación de seguridad del boot.img parcheado...");

        var patchedValidation = BootImageValidator.ValidatePatched(patchedImgLocal, bootImgLocal);
        if (patchedValidation.Status != BootImageValidator.ValidationStatus.Valid)
        {
            LogError($"VALIDACIÓN FALLIDA: {patchedValidation.Message}");
            Log("  No se flasheará un archivo que no pasa las verificaciones de seguridad.");
            Log("  El archivo original está respaldado en: " + bootImgBackup);
            _adbService.Shell(serial, "rm -rf /data/local/tmp/magisk /data/local/tmp/boot_to_patch.img /data/local/tmp/ramdisk.cpio /data/local/tmp/new-boot.img /data/local/tmp/boot_to_patch.img.bak");
            _adbService.RestartAdb();
            return RootMethodStatus.Failed;
        }

        LogOk($"boot.img parcheado: VÁLIDO ({patchedValidation.FileSize / 1024 / 1024} MB)");
        LogOk($"  SHA256: {patchedValidation.Sha256?[..16]}...");

        // Gate combinado anti-brick: NO flashear si ninguna señal confirma Magisk.
        // (La inyección verificada en el dispositivo y las firmas en el binario son independientes:
        //  si AMBAS fallan, el parche no se aplicó y flasheararía un boot sin root.)
        if (patchedValidation.ContainsMagisk)
        {
            LogOk("  Firmas Magisk detectadas en el parche.");
        }
        else if (injectionVerified)
        {
            LogWarning("  Firmas Magisk no visibles en el binario (ramdisk comprimido), pero la inyección se verificó en el dispositivo.");
        }
        else
        {
            LogError("No hay evidencia de Magisk en el boot parcheado (inyección sin verificar y sin firmas).");
            Log("  El parche falló. NO se flasheará este boot.img.");
            Log("  El archivo original está respaldado en: " + bootImgBackup);
            _adbService.Shell(serial, cleanupCmd, ct: ct);
            _adbService.RestartAdb();
            return RootMethodStatus.Failed;
        }
        ct.ThrowIfCancellationRequested();

        // ════════════════════════════════════════════════════
        // PASO 10: Probar sin grabar. El flash permanente es otro paso,
        // solo si el usuario lo confirma y la copia original es válida.
        // ════════════════════════════════════════════════════
        var blBeforeFlash = _adbService.CheckBootloaderUnlock(serial);
        if (!RootSafetyPolicy.MayBeginRoot(AdbService.ParseBootloaderUnlocked(blBeforeFlash.Output)))
        {
            LogError("Bootloader bloqueado o no confirmado. NO se reinicia ni se flashea.");
            Log("  Con el bootloader cerrado no se puede rootear, y desbloquearlo BORRA los datos.");
            Log("  El teléfono sigue en Android. No se ha borrado nada.");
            Log($"  Boot original: {bootImgBackup}");
            Log($"  Boot parcheado, NO flasheado: {patchedImgLocal}");
            _adbService.RestartAdb();
            return RootMethodStatus.Failed;
        }

        var isSamsung = deviceInfo.Manufacturer.Contains("samsung", StringComparison.OrdinalIgnoreCase);
        var partition = bootPart.FastbootPartition;
        var isInitBoot = RootSafetyPolicy.IsInitBootPartition(partition);
        var originalOk = BootImageValidator.ValidateOriginal(bootImgBackup).Status == BootImageValidator.ValidationStatus.Valid;
        if (!originalOk || !RootSafetyPolicy.IsFlashableBootPartition(partition))
        {
            LogError("Falta una copia original válida o la partición no es boot/init_boot. No se reinicia.");
            return RootMethodStatus.Failed;
        }

        var fingerprint = _adbService.ExecuteAdb($"-s {serial} shell getprop ro.build.fingerprint", ct: ct, timeoutMs: 8000);
        var session = new BootSession
        {
            Serial = serial,
            OriginalPath = bootImgBackup,
            PatchedPath = patchedImgLocal,
            FastbootPartition = partition,
            Fingerprint = fingerprint.Success ? fingerprint.Output.Trim() : null,
            IsInitBoot = isInitBoot,
            PatchEvidence = patchedValidation.ContainsMagisk || injectionVerified,
            TempBootVerified = false,
            UseDownloadMode = isSamsung
        };

        if (isSamsung)
        {
            Log("\nPASO 10: Samsung no tiene fastboot. Se graba por Download Mode (Odin/heimdall).");
            Log("  No se prueba en RAM. No se formatea otra vez: el bootloader ya está abierto.");
        }
        else if (!isInitBoot)
        {
            Log("\nPASO 10: Prueba temporal (fastboot boot). No graba la partición.");
            _adbService.RebootToBootloader(serial);
            if (!_adbService.WaitForFastbootDevice(serial, 45, ct))
            {
                LogError("No entró en fastboot. No se ha flasheado nada.");
                _adbService.RestartAdb();
                return RootMethodStatus.Failed;
            }
            ct.ThrowIfCancellationRequested();

            if (!_adbService.BootImageViaFastboot(serial, patchedImgLocal))
            {
                LogError("fastboot boot rechazó la imagen. NO se flashea.");
                Log("  Reiniciando al sistema que ya estaba instalado.");
                _adbService.FastbootReboot(serial);
                _adbService.RestartAdb();
                return RootMethodStatus.Failed;
            }

            Log("  Imagen aceptada en RAM. Esperando Android para comprobar uid=0...");
            _adbService.RestartAdb();
            session.TempBootVerified = await WaitForRealRootAsync(serial, ct, 90);
            if (!session.TempBootVerified)
            {
                LogError("La prueba no dio root real (uid=0). NO se graba la partición.");
                Log("  Un reinicio vuelve al sistema de siempre. Los datos no se han borrado.");
                RebootBackToInstalledSystem(serial);
                return RootMethodStatus.Failed;
            }
            LogOk("Root temporal verificado. Se graba ahora, sin otra pregunta.");
        }
        else
        {
            Log("\nPASO 10: Este teléfono usa init_boot.");
            Log("  No se puede probar con fastboot boot. Se graba solo esa partición.");
            Log("  No formatea el teléfono. Si no arranca, Restaurar boot vuelve al original.");
        }

        SaveBootSession(session);
        Log($"  Original: {bootImgBackup}");
        Log($"  Parche: {patchedImgLocal}");
        Log($"  Partición: {partition}");
        return await CommitPatchedBootAsync(serial, ct);
    }
private async Task<RootMethodStatus> AdbExploitRootAsync(string serial, CancellationToken ct)
    {
        Log("═══════════════════════════════════════════");
        Log("     ROOT VÍA ADB / EXPLOIT");
        Log("═══════════════════════════════════════════");

        var deviceInfo = await _adbService.GetDeviceInfoAsync(serial);
        if (deviceInfo != null)
        {
            Log($"Dispositivo: {deviceInfo.Manufacturer} {deviceInfo.Model}");
            Log($"Android: {deviceInfo.AndroidVersion} | ABI: {deviceInfo.Abi}");
        }

        // 'adb root' reinicia adbd en builds userdebug/eng (desconecta el device
        // a mitad del escaneo). Solo intentarlo si ro.debuggable=1, y esperar a
        // que adbd vuelva antes de comprobar su.
        Log("\n[1/5] Probando 'adb root'...");
        var debuggable = _adbService.ExecuteAdb($"-s {serial} shell getprop ro.debuggable", ct: ct, timeoutMs: 5000);
        var isDebuggableBuild = debuggable.Success && debuggable.Output.Trim() == "1";

        AdbCommandResult? adbRoot = null;
        if (isDebuggableBuild)
            adbRoot = _adbService.ExecuteAdb($"-s {serial} root", ct: ct, timeoutMs: 10000);

        if (adbRoot != null && adbRoot.Success &&
            (adbRoot.Output.Contains("already running as root") ||
             adbRoot.Output.Contains("restarting") ||
             adbRoot.Output.Contains("adbd is now running as root")))
        {
            if (adbRoot.Output.Contains("restarting"))
            {
                Log("  adbd reiniciando — esperando a que el dispositivo vuelva...");
                for (int i = 0; i < 20 && !ct.IsCancellationRequested; i++)
                {
                    await Task.Delay(500, ct);
                    if (_adbService.GetConnectedDevices().Contains(serial))
                        break;
                }
                _adbService.RestartAdb();
            }

            var suCheck = _adbService.ExecuteAdb($"-s {serial} shell su -c id", ct: ct);
            if (suCheck.Success && (suCheck.Output.Contains("uid=0") || suCheck.Output.Contains("root")))
            {
                Log("  ✅ 'adb root' funcionó. Dispositivo ya es root.");
                Log("  Instalando Magisk companion desde MagiskService...");
                InstallMagiskCompanion(serial, ct);
                return RootMethodStatus.Success;
            }
        }
        else if (!isDebuggableBuild)
        {
            Log("  Build de producción (ro.debuggable=0) — 'adb root' no disponible; omitido.");
        }
        else
        {
            Log("  'adb root' no disponible en este dispositivo.");
        }

        ct.ThrowIfCancellationRequested();

        Log("\n[2/5] Intentando remontar /system como lectura/escritura...");
        var mountResult = _adbService.Shell(serial, "mount -o rw,remount /system 2>&1", ct: ct);
        if (mountResult.Success)
            Log("  /system montado como RW.");
        else
        {
            mountResult = _adbService.Shell(serial, "mount -o rw,remount / 2>&1", ct: ct);
            if (mountResult.Success)
                Log("  Partición raíz '/' montada como RW.");
            else
                Log("  No se pudo remontar /system (probablemente SELinux estricto o bootloader bloqueado).");
        }

        Log("\n[3/5] Verificando si ya existe 'su' en el sistema...");
        ct.ThrowIfCancellationRequested();

        var suTest = _adbService.ExecuteAdb($"-s {serial} shell su -c id", ct: ct);
        if (suTest.Success && (suTest.Output.Contains("uid=0") || suTest.Output.Contains("root")))
        {
            Log("  ✅ 'su' ya está funcional en el dispositivo.");
            return RootMethodStatus.Success;
        }
        Log("  'su' no encontrado o no funcional.");

        Log("\n[4/5] Preparando Magisk para deploy vía ADB...");
        if (!_magiskService.HasBinaries)
        {
            Log("  Descargando Magisk para obtener el binario 'su'...");
            var dl = await _magiskService.DownloadMagiskAsync();
            if (!dl)
            {
                Log("  ❌ No se pudo descargar Magisk.");
                return RootMethodStatus.Failed;
            }
        }

        var abi = deviceInfo?.Abi ?? "arm64-v8a";
        var is64Bit = abi.Contains("64") || abi.Contains("arm64") || abi == "x86_64";
        var suBinaryName = is64Bit ? "magisk64" : "magisk32";
        var suLocalPath = Path.Combine(_magiskService.MagiskDir, suBinaryName);

        if (!File.Exists(suLocalPath))
        {
            Log($"  ❌ Binario '{suBinaryName}' no encontrado después de descargar Magisk.");
            return RootMethodStatus.Failed;
        }

        Log($"  Subiendo {suBinaryName} como 'su' al dispositivo...");
        _adbService.PushFile(serial, suLocalPath, "/data/local/tmp/su");
        _adbService.Shell(serial, "chmod 6755 /data/local/tmp/su", ct: ct);

        var testResult = _adbService.Shell(serial, "/data/local/tmp/su -c id", ct: ct);
        if (testResult.Success && testResult.Output.Contains("uid=0"))
        {
            Log("  ✅ 'su' funcional en /data/local/tmp.");

            Log("\n[5/5] Intentando copiar 'su' a /system/xbin/ (persistente)...");
            _adbService.Shell(serial, "mkdir -p /system/xbin", ct: ct);
            var cpResult = _adbService.Shell(serial, "cp /data/local/tmp/su /system/xbin/su 2>&1", ct: ct);
            if (cpResult.Success)
            {
                _adbService.Shell(serial, "chmod 6755 /system/xbin/su", ct: ct);
                Log("  ✅ 'su' copiado a /system/xbin/su (persistente).");
                _adbService.Shell(serial, "/system/xbin/su -c setenforce 0 2>/dev/null", ct: ct);
                Log("  SELinux puesto en permisivo para permitir el root.");
            }
            else
            {
                Log("  No se pudo escribir en /system/xbin (revisa SELinux o permisos).");
                Log("  El root temporal en /data/local/tmp seguirá funcionando hasta reiniciar.");
            }
            return RootMethodStatus.Success;
        }

Log("  ❌ 'su' no funcionó. Necesitas desbloquear bootloader e intentar Magisk Patch.");
        return RootMethodStatus.Failed;
    }

    /// <summary>
    /// Gate de batería fail-closed: exige lectura fiable ≥ 30% antes de
    /// procesos largos (wipe/flash) para no apagarse a mitad del proceso.
    /// Antes un "Desconocido" saltaba el check (fail-open).
    /// </summary>
    private async Task<RootMethodStatus> SamsungUnlockViaDownloadModeAsync(string serial, CancellationToken ct, string backupDir)
    {
        var allowed = _adbService.ExecuteAdb($"-s {serial} shell getprop sys.oem_unlock_allowed", ct: ct, timeoutMs: 8000);
        if (!RootSafetyPolicy.SamsungOemUnlockSwitchOn(allowed.Success ? allowed.Output : null))
        {
            LogError("Samsung: activa 'Desbloqueo OEM' en Opciones de desarrollador y pulsa Root otra vez.");
            Log("  Sin ese interruptor el teléfono no deja desbloquear. No se ha reiniciado.");
            Log("  El interruptor no significa que ya esté desbloqueado.");
            var other = _adbService.ExecuteAdb($"-s {serial} shell getprop ro.boot.other.locked", ct: ct, timeoutMs: 8000);
            if (other.Success && other.Output.Trim() == "1")
                LogWarning("One UI reciente puede haber ocultado el interruptor. En ese caso este modelo no deja desbloquear por el método oficial.");
            Log($"  Backup parcial, por si lo quieres: {backupDir}");
            return RootMethodStatus.Failed;
        }

        Log("Mandando el Samsung a Download Mode...");
        Log("  EN EL TELÉFONO: mantén Volumen ARRIBA para confirmar el desbloqueo.");
        LogWarning("Eso borra el teléfono y desactiva Knox para siempre (Samsung Pay, Secure Folder).");
        _adbService.RebootToDownload(serial);

        Log("Esperando a que Android vuelva por USB (hasta 10 minutos)...");
        var back = await WaitForDeviceReadyAsync(serial, ct, maxWaitSeconds: 600);
        ct.ThrowIfCancellationRequested();
        if (!back)
        {
            LogError("El Samsung no volvió por ADB. Si ya confirmaste con Volumen Arriba, el borrado ya ocurrió.");
            Log("  1. Termina el asistente de Android");
            Log("  2. Activa Depuración USB y acepta la clave del PC");
            Log("  3. Pulsa Root otra vez: esa vez ya no formatea, instala Magisk por Download Mode.");
            return RootMethodStatus.Failed;
        }

        var parsed = AdbService.ParseBootloaderUnlocked(_adbService.CheckBootloaderUnlock(serial).Output);
        if (!RootSafetyPolicy.MayBeginRoot(parsed))
        {
            LogError("El teléfono volvió, pero el bootloader sigue cerrado. ¿Pulsaste Volumen Arriba en la pantalla de aviso?");
            return RootMethodStatus.Failed;
        }

        LogOk("Samsung desbloqueado. Sigue la instalación de Magisk.");
        return RootMethodStatus.Success;
    }

    private async Task<RootMethodStatus> FlashSamsungDownloadAsync(string serial, BootSession session, CancellationToken ct)
    {
        var pit = RootSafetyPolicy.HeimdallPitName(session.FastbootPartition);
        var heimdall = FindHeimdall();
        if (pit == null || heimdall == null)
        {
            LogError(heimdall == null
                ? "Falta heimdall.exe, que es quien habla el protocolo Odin."
                : "La partición no es boot ni init_boot. No se flashea.");
            Log("  Copia heimdall.exe en la carpeta tools junto a este programa y pulsa Root otra vez.");
            Log("  No se ha reiniciado a Download Mode. El bootloader, si ya estaba abierto, sigue igual.");
            Log($"  Parche listo: {session.PatchedPath}");
            Log($"  Original: {session.OriginalPath}");
            return RootMethodStatus.Failed;
        }

        Log($"Grabando {pit} por Download Mode. No se toca userdata.");
        _adbService.RebootToDownload(serial);
        Log("  Si aparece una pantalla de aviso, pulsa Volumen Arriba para entrar (ya no desbloquea: solo permite flashear).");

        var seen = false;
        for (var i = 0; i < 90 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(1000, ct);
            if (i % 15 == 0 && i > 0)
                Log($"    Esperando Download Mode... ({i}s)");
            if (RunHeimdall(heimdall, "detect", 8000, ct).ExitCode == 0)
            {
                seen = true;
                break;
            }
        }
        if (!seen)
        {
            LogError("No apareció el Samsung en Download Mode. No se ha flasheado.");
            _adbService.RestartAdb();
            return RootMethodStatus.Failed;
        }

        var flash = RunHeimdall(heimdall, $"flash --{pit} \"{session.PatchedPath}\" --no-reboot", 180000, ct);
        if (flash.ExitCode != 0)
        {
            LogError("heimdall no pudo grabar. No se reintenta en otra partición.");
            Log(flash.Output);
            _adbService.RestartAdb();
            return RootMethodStatus.Failed;
        }

        LogOk($"{pit} grabado. Reiniciando el Samsung...");
        RunHeimdall(heimdall, "reboot", 20000, ct);
        _adbService.RestartAdb();
        var verified = await WaitForRealRootAsync(serial, ct, 120);
        if (!verified)
        {
            LogError("Se grabó, pero no hay uid=0. Usa Restaurar boot si no arranca.");
            return RootMethodStatus.Failed;
        }

        LogOk("Samsung con root verificado. Los datos de esta sesión no se han vuelto a borrar.");
        return RootMethodStatus.Success;
    }

    private static string? FindHeimdall()
    {
        var beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "heimdall.exe");
        if (File.Exists(beside)) return beside;
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "heimdall.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private (int ExitCode, string Output) RunHeimdall(string exe, string arguments, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return (-1, "no se pudo arrancar heimdall");
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (-1, "heimdall tardó demasiado");
            }
            var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(output))
                Log(output.Trim());
            return (proc.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>
    /// null = seguir con el parche. Un estado = parar.
    /// </summary>
    private async Task<RootMethodStatus?> EnsureBootloaderForRootAsync(string serial, CancellationToken ct)
    {
        var parsed = AdbService.ParseBootloaderUnlocked(_adbService.CheckBootloaderUnlock(serial).Output);
        if (RootSafetyPolicy.MayBeginRoot(parsed))
        {
            LogOk("Bootloader ya desbloqueado. No se formatea.");
            return null;
        }

        if (!RootSafetyPolicy.MayUnlockDuringRoot(parsed))
        {
            LogError("No se puede confirmar que el bootloader esté cerrado.");
            Log("  No se desbloquea a ciegas y no se flashea. El teléfono no se ha modificado.");
            return RootMethodStatus.Failed;
        }

        LogWarning("Bootloader CERRADO. El root lo desbloquea ahora.");
        LogWarning("Esto BORRA fotos, apps y cuentas. El backup no las recupera.");
        Log("  Si el teléfono lo pide, confirma con Volumen+.");
        ct.ThrowIfCancellationRequested();

        var unlock = await UnlockBootloaderAsync(serial, ct);
        if (unlock == RootMethodStatus.WaitingDevice)
            return RootMethodStatus.WaitingDevice;
        if (unlock != RootMethodStatus.Success)
            return RootMethodStatus.Failed;

        Log("Esperando a que el teléfono vuelva después del formateo...");
        var back = await WaitForDeviceReadyAsync(serial, ct);
        ct.ThrowIfCancellationRequested();
        if (!back)
        {
            LogError("El teléfono no volvió por ADB. El desbloqueo ya borró los datos.");
            Log("  1. Termina el asistente de Android");
            Log("  2. Activa Depuración USB y acepta la clave RSA");
            Log("  3. Pulsa otra vez Root. Esa segunda vez ya no formatea: solo instala Magisk.");
            return RootMethodStatus.Failed;
        }

        var again = AdbService.ParseBootloaderUnlocked(_adbService.CheckBootloaderUnlock(serial).Output);
        if (!RootSafetyPolicy.MayBeginRoot(again))
        {
            LogError("Después del desbloqueo el bootloader no figura como abierto. No se flashea.");
            return RootMethodStatus.Failed;
        }

        LogOk("Bootloader abierto y el teléfono responde. Sigue el parche.");
        return null;
    }

    private async Task<bool> EnsureMinBatteryAsync(string serial, DeviceInfo? deviceInfo = null)
    {
        deviceInfo ??= await _adbService.GetDeviceInfoAsync(serial);
        var raw = (deviceInfo?.BatteryLevel ?? string.Empty).Replace("%", "").Trim();
        if (!int.TryParse(raw, out var battery))
        {
            LogError($"No se pudo leer el nivel de batería ({deviceInfo?.BatteryLevel ?? "sin datos"}).");
            Log("  Se requiere batería ≥ 30% para no apagarse a mitad del proceso.");
            Log("  Revisa la conexión ADB/USB y el teléfono, y reintenta.");
            return false;
        }
        if (battery < 30)
        {
            LogError($"Batería insuficiente ({battery}%). Mínimo requerido: 30%.");
            Log("  Conecta el cargador antes de continuar para evitar apagado durante el flash.");
            return false;
        }
        return true;
    }

    private async Task<RootMethodStatus> UnlockBootloaderAsync(string serial, CancellationToken ct)
    {
        Log("═══════════════════════════════════════════");
        Log("     DESBLOQUEO DE BOOTLOADER");
        Log("═══════════════════════════════════════════");
        Log("ADVERTENCIA: Esto borrará TODOS los datos del dispositivo.");
        Log("Se intenta un backup parcial antes. Si no copia nada, se cancela y no se reinicia.");

        var manufacturer = _adbService.ExecuteAdb($"-s {serial} shell getprop ro.product.manufacturer").Output.Trim().ToLowerInvariant();
        Log($" Fabricante detectado: {manufacturer}");

        // Gate de batería antes de backup/wipe (fail-closed)
        if (!await EnsureMinBatteryAsync(serial))
            return RootMethodStatus.Failed;

        if (manufacturer == "samsung")
        {
            Log("\nSamsung sí se puede rootear. No usa fastboot: el desbloqueo es Download Mode.");
            Log("  Tras el backup el teléfono se reinicia solo a esa pantalla.");

            var samsungOemCheck = _adbService.ExecuteAdb($"-s {serial} shell getprop ro.boot.other.locked");
            var isMediatek = await _adbService.DetectIsMediaTekAsync(serial);

            if (samsungOemCheck.Success && samsungOemCheck.Output.Trim() == "1")
            {
                LogWarning("ro.boot.other.locked=1 detectado — One UI 8 deshabilitó el toggle OEM.");
                LogWarning("Si el toggle 'Desbloqueo OEM' no aparece en Opciones de desarrollador:");
                if (isMediatek)
                {
                    LogOk("Tu dispositivo usa chip MediaTek → compatible con MTKClient.");
                    LogOk("Usa el método 'MTKClient Unlock' de esta herramienta como alternativa.\n");
                }
                else
                {
                    Log("  Revisa en XDA si hay un método alternativo para tu modelo.\n");
                }
            }

            Log("  Pasos (si el toggle está disponible):");
            Log("    1. Ajustes > Opciones de desarrollador > Activar 'Desbloqueo OEM'");
            Log("    2. Apaga el teléfono");
            Log("    3. Conecta USB al PC, pulsa VOL- + VOL+ y conecta el USB");
            Log("    4. Pulsa VOL+ largo para desbloquear");
            Log("    5. El teléfono se resetea solo\n");
            LogWarning("Knox se dispara permanentemente -> Samsung Pay/Secure Folder dejan de funcionar\n");
            Log("  ¿Quieres continuar con el backup y el proceso de desbloqueo?");
            Log("  Si prefieres hacerlo manual, cierra esta herramienta y sigue los pasos.\n");
        }

        // Carpeta nueva: una reutilizada contaba archivos viejos como backup de hoy
        // y autorizaba el formateo aunque este intento no hubiera copiado nada.
        var backupDir = CreateFreshBackupDirectory(serial);

        Dictionary<string, string> apkResult = new();
        List<string> mediaResult = new();
        List<string> docsResult = new();

        Log("\n=== FASE 1: Backup automático de datos del cliente ===");

            Log("\n[1/4] Backup de apps (.apk) instaladas...");
            ct.ThrowIfCancellationRequested();
            apkResult = await BackupAppsAsync(serial, backupDir, ct);
            Log($"  Apps respaldadas: {apkResult.Count}");

            Log("\n[2/4] Backup de fotos/vídeos...");
            ct.ThrowIfCancellationRequested();
            mediaResult = await BackupMediaAsync(serial, backupDir, ct);
            Log($"  Archivos multimedia respaldados: {mediaResult.Count}");

            Log("\n[3/4] Backup de documentos y archivos importantes...");
            ct.ThrowIfCancellationRequested();
            docsResult = await BackupDocumentsAsync(serial, backupDir, ct);
            Log($"  Documentos respaldados: {docsResult.Count}");

            Log("\n[4/4] Backup de contactos y SMS...");
            ct.ThrowIfCancellationRequested();
            await BackupContactsSmsAsync(serial, backupDir, ct);

            // Gate: no formatear si este intento no copió apps, fotos o documentos.
            // Un contacts.txt o archivos de un backup anterior no cuentan.
            if (!RootSafetyPolicy.BackupHasUserFiles(apkResult.Count, mediaResult.Count, docsResult.Count))
            {
                LogError("❌ Backup sin apps, fotos ni documentos — se CANCELA el desbloqueo.");
                Log("  No se reinicia el teléfono. Tus datos siguen intactos.");
                Log("  El backup automático es parcial e incompleto; si quieres conservar");
                Log("  los datos, cópialos tú al PC y no desbloquees el bootloader.");
                return RootMethodStatus.Failed;
            }

            Log($"\n✅ Backup parcial en: {backupDir}");
            Log($"  Apps (solo APK, sin sus datos): {apkResult.Count}");
            Log($"  Fotos/vídeos (tope bajo): {mediaResult.Count}");
            Log($"  Documentos (tope bajo): {docsResult.Count}");
            LogWarning("ESTO NO ES UNA COPIA DEL TELÉFONO.");
            LogWarning("No incluye datos de apps, cuentas, chats ni la mayoría de archivos.");
            LogWarning("El desbloqueo va a BORRAR el teléfono igualmente.");

        // Cancelación durante el backup no debe llegar hasta aquí tragada:
        // abortar ANTES de la fase de wipe.
        ct.ThrowIfCancellationRequested();

        Log("\n=== FASE 2: Verificación de OEM Unlock ===");
        Log("\nVerificando si OEM Unlock está habilitado...");
        var oemCheck = _adbService.ExecuteAdb($"-s {serial} shell getprop ro.oem_unlock_supported");
        if (oemCheck.Success && oemCheck.Output.Trim() == "1")
        {
            Log("✓ OEM Unlock soportado por el dispositivo.");
        }
        else
        {
            Log("⚠ No se pudo confirmar soporte OEM Unlock.");
            Log("Asegúrate de haber habilitado:");
            Log("  1. Ajustes > Acerca del teléfono > Toca 'Número de compilación' 7 veces");
            Log("  2. Ajustes > Sistema > Opciones de desarrollador > 'Desbloqueo OEM'");
            Log("  Si no lo habilitas, el comando fallará en la pantalla del teléfono.");
        }

        if (manufacturer == "samsung")
            return await SamsungUnlockViaDownloadModeAsync(serial, ct, backupDir);

        Log("\n=== FASE 3: Desbloqueo del bootloader ===");
        Log("\nReiniciando a modo bootloader/fastboot...");
        _adbService.RebootToBootloader(serial);
        Log("  Esperando dispositivo en fastboot...");

        var fastbootReady = _adbService.WaitForFastbootDevice(serial, 30, ct);
        if (!fastbootReady)
        {
            Log("ERROR: No se detecta el dispositivo en modo fastboot.");
            Log("  Asegúrate de: conexión USB directa, drivers instalados,");
            Log("  modo bootloader activado (Volumen- + Encendido).");
            _adbService.RestartAdb();
            return RootMethodStatus.Failed;
        }
        Log("  Dispositivo detectado en modo fastboot.");
        ct.ThrowIfCancellationRequested();

        Log("\nConsultando estado actual del bootloader...");
        var blInfo = _adbService.CheckBootloaderUnlock(serial);
        Log($"  {blInfo.Output.Trim()}");
        if (AdbService.ParseBootloaderUnlocked(blInfo.Output) == true)
        {
            Log("\n✅ El bootloader YA ESTÁ DESBLOQUEADO.");
            _adbService.FastbootReboot(serial);
            return RootMethodStatus.Success;
        }

        Log("\nIntentando desbloquear bootloader...");
        Log("  ⚠ REVISA LA PANTALLA DEL TELÉFONO");
        Log("  Debes confirmar con las teclas de volumen (Volumen+)");
        Log("  y el botón de encendido en la pantalla del teléfono.");

        // Timeout 120s: el usuario debe confirmar físicamente en la pantalla del teléfono
        var unlockResult = _adbService.ExecuteFastboot($"-s {serial} oem unlock", timeoutMs: 120000);
        if (unlockResult.Success)
            goto UnlockSuccess;

        Log($"  Respuesta: {unlockResult.Output.Trim()}");

        Log("  Método 2: fastboot flashing unlock...");
        Log("  ⚠ REVISA LA PANTALLA DEL TELÉFONO — posible confirmación requerida");

        unlockResult = _adbService.ExecuteFastboot($"-s {serial} flashing unlock", timeoutMs: 120000);
        if (unlockResult.Success)
            goto UnlockSuccess;

        Log($"  Respuesta: {unlockResult.Output.Trim()}");

        Log("  Método 3: fastboot flashing unlock_critical...");

        unlockResult = _adbService.ExecuteFastboot($"-s {serial} flashing unlock_critical", timeoutMs: 120000);
        if (unlockResult.Success)
            goto UnlockSuccess;

        Log($"  Respuesta: {unlockResult.Output.Trim()}");

        Log("\nMÉTODOS ADICIONALES (según fabricante):");

        Log($"  Para Xiaomi: fastboot -s {serial} oem unlock");
        Log("    Necesitas desbloquear la cuenta Mi antes en: https://en.miui.com/unlock/");
        Log($"    Luego en fastboot: fastboot -s {serial} oem unlock");

        Log($"  Para Huawei: suele requerir código de desbloqueo por solicitud a Huawei");
        Log("    Huawei no soporta desbloqueo oficial en muchos modelos nuevos.");

        Log($"  Para Motorola: fastboot -s {serial} oem unlock [código]");
        Log("    Solicita código en: https://motorola-global-portal.custhelp.com/app/standalone/bootloader/reveal");

        Log($"  Para Sony: fastboot -s {serial} oem unlock 0x[clave]");
        Log("    Solicita código a Sony; algunos modelos pueden usar: fastboot flashing unlock");

        Log($"  Para OnePlus/Nothing: fastboot -s {serial} oem unlock");
        Log("    Si pide confirmación, acepta con Vol+ y Encendido");

        Log("\n  Si tu dispositivo mostró un mensaje en pantalla, ");
        Log("  confírmalo con las teclas de volumen y el botón de encendido.");
        Log("  Luego vuelve a ejecutar este método.");

        _adbService.FastbootReboot(serial);
        _adbService.RestartAdb();
        return RootMethodStatus.Failed;

    UnlockSuccess:
        Log("\n═══════════════════════════════════════════");
        Log("  ✅ BOOTLOADER DESBLOQUEADO EXITOSAMENTE");
        Log("═══════════════════════════════════════════");
        Log("  Los datos del dispositivo fueron BORRADOS (factory reset).");
        Log("  Esto es normal — configúralo de nuevo como dispositivo nuevo.");
        Log($"  El backup parcial está en: {backupDir}");
        LogWarning("No recupera cuentas, chats ni datos de las apps.");

        try
        {
            var summary = new
            {
                backupDir = backupDir,
                serial = serial,
                timestamp = DateTime.Now,
                appsCount = apkResult.Count,
                mediaCount = mediaResult.Count,
                documentsCount = docsResult.Count,
                appHashes = apkResult // package -> sha256
            };
            var summaryPath = Path.Combine(backupDir, "backup_summary.json");
            await File.WriteAllTextAsync(summaryPath, 
                System.Text.Json.JsonSerializer.Serialize(summary, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), ct);
            Log($"  Resumen del backup guardado en: {summaryPath}");
            
            // Create ZIP archive for easier restoration
            try
            {
                var zipPath = backupDir + ".zip";
                if (File.Exists(zipPath)) File.Delete(zipPath);
                Log("  Creando archivo comprimido del backup...");
                ZipFile.CreateFromDirectory(backupDir, zipPath, CompressionLevel.Optimal, false);
                Log($"  Backup comprimido guardado en: {zipPath}");
                Log($"  Tamaño: {new FileInfo(zipPath).Length / 1024 / 1024} MB");
            }
            catch (Exception ex)
            {
                Log($"  Error al crear ZIP: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Log($"  Error al crear resumen: {ex.Message}");
        }

        Log("\nReiniciando dispositivo...");
        _adbService.FastbootReboot(serial);
        _adbService.RestartAdb();
        Log("  El dispositivo se reiniciará. Puede tardar varios minutos en el primer arranque.");

        Log("\nPRÓXIMOS PASOS:");
        Log("  1. Configura el dispositivo (idioma, WiFi, cuenta Google)");
        Log("  2. Habilita 'Depuración USB' en Opciones de desarrollador");
        Log("  3. Usa 'Magisk Patch' para rootear el dispositivo");
        Log("  4. Restaura datos desde el backup si es necesario");

        return RootMethodStatus.Success;
    }

    private async Task<Dictionary<string, string>> BackupAppsAsync(string serial, string backupDir, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var apps = new Dictionary<string, string>(); // package -> sha256
            var apkDir = Path.Combine(backupDir, "apps");
            Directory.CreateDirectory(apkDir);
            var packages = new List<string>();

            try
            {
                var packagesResult = _adbService.ExecuteAdb($"-s {serial} shell pm list packages -3");
                if (!packagesResult.Success)
                {
                    Log("    ERROR: No se pudo listar paquetes del dispositivo.");
                    return apps;
                }

                packages = packagesResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim().Replace("package:", ""))
                    .Where(p => !string.IsNullOrEmpty(p) && AdbService.IsValidPackageName(p)).ToList();

                Log($"    Encontradas {packages.Count} apps instaladas. Procesando hasta 50...");
                var toProcess = packages.Take(50).ToList();
                var count = 0;

                foreach (var package in toProcess)
                {
                    ct.ThrowIfCancellationRequested();
                    count++;

                    var pathResult = _adbService.ExecuteAdb($"-s {serial} shell pm path {package}", ct: ct);
                    if (!pathResult.Success) continue;

                    var apkPaths = pathResult.Output
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim().Replace("package:", "").Trim())
                        .Where(p => p.EndsWith(".apk") && p.Contains("base.apk"))
                        .ToList();

                    if (apkPaths.Count == 0) continue;
                    var apkPath = apkPaths[0];

                    // Use package name directly as filename (already validated as safe)
                    var fileName = $"{package}.apk";
                    var targetPath = Path.Combine(apkDir, fileName);
                    var pullResult = _adbService.PullFile(serial, apkPath, targetPath);
                    if (pullResult.Success)
                    {
                        var fileInfo = new FileInfo(targetPath);
                        if (fileInfo.Length > 1000)
                        {
                            var hash = CalculateFileSha256(targetPath);
                            apps[package] = hash;
                            Log($"    [{count}/{toProcess.Count}] Guardada: {package} (SHA256: {hash[..12]}...)");
                        }
                        else
                        {
                            File.Delete(targetPath);
                            Log($"    [{count}/{toProcess.Count}] Archivo vacío, omitido: {package}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"    Error al respaldar apps: {ex.Message}");
            }

            Log($"    Apps respaldadas: {apps.Count}/{packages.Count}");
            return apps;
        }, ct);
    }

    private static string CalculateFileSha256(string filePath)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    // Hash estable entre procesos/ejecuciones (GetHashCode varía por proceso →
    // nombres de backup no deterministas al restaurar en otra sesión).
    private static string StableNameHash(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes.AsSpan(0, 4));
    }

    private async Task<List<string>> BackupMediaAsync(string serial, string backupDir, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var files = new List<string>();
            var mediaDir = Path.Combine(backupDir, "media");
            Directory.CreateDirectory(mediaDir);

            var commonPaths = new[]
            {
                "/sdcard/DCIM", "/sdcard/Pictures", "/sdcard/Download", "/sdcard/Documents"
            };

            foreach (var basePath in commonPaths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Log($"    Explorando {basePath}...");
                    
                    var listResult = _adbService.ExecuteAdb($"-s {serial} shell \"find {basePath} -type f \\( -name '*.jpg' -o -name '*.jpeg' -o -name '*.png' -o -name '*.mp4' -o -name '*.pdf' \\) 2>/dev/null | head -50\"", ct: ct);
                    if (!listResult.Success) continue;

                    foreach (var line in listResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        ct.ThrowIfCancellationRequested();
                        var remotePath = line.Trim();
                        if (string.IsNullOrEmpty(remotePath) || remotePath.Contains("No such file")) continue;

                        var fileName = AdbService.SanitizeFileName(Path.GetFileName(remotePath));
                        if (string.IsNullOrEmpty(fileName) || fileName == "unknown") continue;

                        // Use unique filename to prevent overwrite: hash estable + original name
                        var uniqueName = $"{StableNameHash(remotePath)}_{fileName}";
                        var targetPath = Path.Combine(mediaDir, uniqueName);
                        var pullResult = _adbService.PullFile(serial, remotePath, targetPath);
                        if (pullResult.Success)
                        {
                            files.Add(uniqueName);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch { }
            }

            Log($"    Archivos multimedia respaldados: {files.Count}");
            return files;
        }, ct);
    }

    private async Task<List<string>> BackupDocumentsAsync(string serial, string backupDir, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var files = new List<string>();
            var docsDir = Path.Combine(backupDir, "documents");
            Directory.CreateDirectory(docsDir);

            var docPaths = new[]
            {
                "/sdcard/Documents", "/sdcard/Download", "/sdcard/WhatsApp"
            };

            foreach (var basePath in docPaths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Log($"    Buscando documentos en {basePath}...");
                    
            var listResult = _adbService.ExecuteAdb($"-s {serial} shell \"find {basePath} -type f \\( -name '*.pdf' -o -name '*.doc' -o -name '*.docx' -o -name '*.xls' -o -name '*.xlsx' -o -name '*.txt' \\) 2>/dev/null | head -30\"", ct: ct);
                    if (!listResult.Success) continue;

                    foreach (var line in listResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                    ct.ThrowIfCancellationRequested();
                    var remotePath = line.Trim();
                    if (string.IsNullOrEmpty(remotePath) || remotePath.Contains("No such file")) continue;

                    var fileName = AdbService.SanitizeFileName(Path.GetFileName(remotePath));
                    if (string.IsNullOrEmpty(fileName) || fileName == "unknown") continue;

                    // Use unique filename to prevent overwrite (hash estable entre procesos)
                    var uniqueName = $"{StableNameHash(remotePath)}_{fileName}";
                    var targetPath = Path.Combine(docsDir, uniqueName);
                    var pullResult = _adbService.PullFile(serial, remotePath, targetPath);
                    if (pullResult.Success)
                    {
                        files.Add(uniqueName);
                    }
                }
            }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch { }
        }

        Log($"    Documentos respaldados: {files.Count}");
        return files;
    }, ct);
    }

    private async Task BackupContactsSmsAsync(string serial, string backupDir, CancellationToken ct)
    {
        try
        {
            Log("    Exportando contactos...");
            var contactsResult = _adbService.ExecuteAdb($"-s {serial} shell \"content query --uri content://contacts/phones/ --projection display_name:number\"");
            if (contactsResult.Success && !string.IsNullOrWhiteSpace(contactsResult.Output))
            {
                var contactsFile = Path.Combine(backupDir, "contacts.txt");
                await File.WriteAllTextAsync(contactsFile, contactsResult.Output, ct);
                Log($"    Contactos guardados en {contactsFile}");
            }

            Log("    Intentando backup de SMS...");
            
            // Try to backup SMS database (requires appropriate permissions)
            var smsPaths = new[]
            {
                "/data/data/com.android.providers.telephony/databases/mmssms.db",
                "/sdcard/Android/data/com.android.providers.telephony/databases/mmssms.db"
            };

            foreach (var smsPath in smsPaths)
            {
                ct.ThrowIfCancellationRequested();
                var checkResult = _adbService.ExecuteAdb($"-s {serial} shell \"ls {smsPath} 2>/dev/null\"");
                if (!checkResult.Success) continue;

                var smsFile = Path.Combine(backupDir, "sms_backup.db");
                var pullResult = _adbService.PullFile(serial, smsPath, smsFile);
                if (pullResult.Success)
                {
                    Log($"    SMS guardados en {smsFile}");
                    break;
                }
            }

            // Try WhatsApp database as alternative
            var waResult = _adbService.ExecuteAdb($"-s {serial} shell \"ls /sdcard/WhatsApp/Databases/msgstore.db 2>/dev/null\"");
            if (waResult.Success)
            {
                var waFile = Path.Combine(backupDir, "whatsapp_backup.db");
                var waPull = _adbService.PullFile(serial, "/sdcard/WhatsApp/Databases/msgstore.db", waFile);
                if (waPull.Success)
                {
                    Log($"    WhatsApp mensajes guardados en {waFile}");
                }
            }

            Log("    NOTA: Para backup completo de SMS, usa una app como 'SMS Backup & Restore'.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"    Error al respaldar contactos/mensajes: {ex.Message}");
        }
    }

public async IAsyncEnumerable<string> RestoreBackupAsync(string serial, string backupDir, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
     {
         await Task.CompletedTask; // Make method truly async
         Log("═══════════════════════════════════════════");
         Log("     RESTAURACIÓN DE BACKUP");
         Log("═══════════════════════════════════════════");

         if (!Directory.Exists(backupDir))
         {
             Log($"ERROR: El backup no existe en: {backupDir}");
             yield return "ERROR: El directorio de backup no existe";
             yield break;
         }

         // Check for ZIP file
         var zipFile = Directory.GetFiles(Path.GetDirectoryName(backupDir) ?? backupDir, "*.zip")
             .FirstOrDefault(f => f.StartsWith(backupDir));
         
         if (zipFile != null && File.Exists(zipFile))
         {
             Log($"Encontrado archivo comprimido: {zipFile}");
             yield return $"Descomprimiendo backup desde: {zipFile}";
             var extractDir = Path.Combine(Path.GetDirectoryName(zipFile) ?? "", Path.GetFileNameWithoutExtension(zipFile));
             ZipFile.ExtractToDirectory(zipFile, extractDir, overwriteFiles: true);
             backupDir = extractDir;
         }

         Log($"\nRestaurando desde: {backupDir}");
         yield return $"Restaurando desde: {backupDir}";

         // Load backup summary for hash verification
         Dictionary<string, string>? knownHashes = null;
         var summaryPath = Path.Combine(backupDir, "backup_summary.json");
         if (File.Exists(summaryPath))
         {
             try
             {
                 var summaryJson = await File.ReadAllTextAsync(summaryPath, ct);
                 var summaryDoc = JsonDocument.Parse(summaryJson);
                 if (summaryDoc.RootElement.TryGetProperty("appHashes", out var hashesElement)
                     && hashesElement.ValueKind == JsonValueKind.Object)
                 {
                     knownHashes = new Dictionary<string, string>();
                     foreach (var prop in hashesElement.EnumerateObject())
                     {
                         knownHashes[prop.Name] = prop.Value.GetString() ?? "";
                     }
                     Log($"  Hashes de {knownHashes.Count} APKs cargados para verificación.");
                 }
             }
             catch (Exception ex)
             {
                 LogWarning($"  No se pudieron cargar hashes: {ex.Message}");
             }
         }

         // Restore apps
         var appsDir = Path.Combine(backupDir, "apps");
         int apkOk = 0, apkFail = 0;
         if (Directory.Exists(appsDir))
         {
             Log("\n[1/3] Restaurando apps...");
             yield return "Instalando apps desde backup...";
             foreach (var apk in Directory.GetFiles(appsDir, "*.apk"))
             {
                 ct.ThrowIfCancellationRequested();
                 var apkName = Path.GetFileNameWithoutExtension(apk);
                 yield return $"Instalando: {apkName}";
                 Log($"  Instalando: {apkName}");

                 // Verify APK integrity if hashes are available
                 if (knownHashes != null)
                 {
                     var packageName = apkName; // fileName is "{package}.apk"
                     if (knownHashes.TryGetValue(packageName, out var expectedHash))
                     {
                         var currentHash = CalculateFileSha256(apk);
                         if (!currentHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                         {
                             Log($"    ⚠ ADVERTENCIA: Hash SHA256 no coincide para {apkName}");
                             Log($"      Esperado: {expectedHash[..16]}...");
                             Log($"      Obtenido: {currentHash[..16]}...");
                             Log($"      El APK puede estar corrupto o haber sido modificado.");
                             Log($"      Saltando instalación por seguridad.");
                             apkFail++;
                             yield return $"✗ {apkName} omitido (hash no coincide)";
                             continue;
                         }
                         Log($"    SHA256 verificado: {currentHash[..12]}...");
                     }
                     else
                     {
                         LogWarning($"    No hay hash registrado para {apkName}. Instalando sin verificación.");
                     }
                 }

                 var result = _adbService.InstallApk(serial, apk);
                 if (result.Success)
                 {
                     apkOk++;
                     Log("    ✅ Instalada");
                     yield return $"✓ {apkName} instalada";
                 }
                 else
                 {
                     apkFail++;
                     Log($"    ❌ Error: {result.Error}");
                     yield return $"✗ Error instalando {apkName}: {result.Error}";
                 }
             }
         }

         // Restore documents/media
         var mediaDir = Path.Combine(backupDir, "media");
         var docsDir = Path.Combine(backupDir, "documents");
         int pushOk = 0, pushFail = 0;

         if (Directory.Exists(mediaDir) || Directory.Exists(docsDir))
         {
             Log("\n[2/3] Restaurando archivos...");
             yield return "Restaurando archivos multimedia y documentos...";
             var deviceStorage = "/sdcard/Download/PCAndroidRooter_Restored";
             _adbService.Shell(serial, $"mkdir -p {deviceStorage}");

             var allFiles = Enumerable.Empty<string>();
             if (Directory.Exists(mediaDir)) allFiles = allFiles.Concat(Directory.GetFiles(mediaDir));
             if (Directory.Exists(docsDir)) allFiles = allFiles.Concat(Directory.GetFiles(docsDir));

             foreach (var file in allFiles)
             {
                 ct.ThrowIfCancellationRequested();
                 var push = _adbService.PushFile(serial, file, $"{deviceStorage}/{Path.GetFileName(file)}");
                 if (push.Success)
                 {
                     pushOk++;
                 }
                 else
                 {
                     pushFail++;
                     Log($"    ❌ No se pudo restaurar: {Path.GetFileName(file)} ({push.Error})");
                 }
             }
             Log($"  Archivos restaurados: {pushOk}/{pushOk + pushFail}");
             yield return $"Archivos restaurados: {pushOk}/{pushOk + pushFail} en {deviceStorage}";
         }

         // Restore contacts (requires special handling)
         var contactsFile = Path.Combine(backupDir, "contacts.txt");
         if (File.Exists(contactsFile))
         {
             Log("\n[3/3] Contactos detectados en backup.");
             Log("  NOTA: La restauración de contactos requiere importar manualmente");
             Log("  desde la app de Contactos > Importar/Exportar > Archivo VCard");
             yield return "Contactos detectados - restaurar manualmente desde contacts.txt";
         }

         if (pushFail > 0 || apkFail > 0)
         {
             var totalFail = pushFail + apkFail;
             var totalOk = pushOk + apkOk;
             Log($"\n⚠ Restauración INCOMPLETA: {totalOk} correctos, {totalFail} fallidos");
             yield return $"⚠ Restauración incompleta: {totalOk} correctos, {totalFail} fallidos";
         }
         else
         {
             Log("\n✅ Restauración completada");
             Log("  Las apps instaladas aparecerán en la pantalla de inicio");
             Log("  Los archivos están en /sdcard/Download/PCAndroidRooter_Restored");
             yield return "✅ Restauración completada exitosamente";
         }
     }

    private async Task<RootMethodStatus> CustomRecoveryRootAsync(string serial, CancellationToken ct)
    {
        Log("═══════════════════════════════════════════");
        Log("     ROOT VÍA RECOVERY PERSONALIZADO");
        Log("═══════════════════════════════════════════");

        var deviceInfo = await _adbService.GetDeviceInfoAsync(serial);
        if (deviceInfo != null)
        {
            Log($"Dispositivo: {deviceInfo.Manufacturer} {deviceInfo.Model}");
            Log($"Android: {deviceInfo.AndroidVersion} | ABI: {deviceInfo.Abi}");
        }

        Log("\nPASO 1: Verificando bootloader...");
        _adbService.RebootToBootloader(serial);
        var fastbootOk = _adbService.WaitForFastbootDevice(serial, 15, ct);
        if (!fastbootOk)
        {
            Log("ERROR: No se detecta el dispositivo en modo fastboot.");
            _adbService.RestartAdb();
            return RootMethodStatus.Failed;
        }

        var blCheck = _adbService.CheckBootloaderUnlock(serial);
        if (AdbService.ParseBootloaderUnlocked(blCheck.Output) != true)
        {
            Log("ERROR: Bootloader debe estar desbloqueado para flashear recovery.");
            Log("Usa el método 'Desbloquear Bootloader' primero.");
            _adbService.FastbootReboot(serial);
            _adbService.RestartAdb();
            return RootMethodStatus.Failed;
        }
        Log("✓ Bootloader desbloqueado.");
        ct.ThrowIfCancellationRequested();

        Log("\nPASO 2: Descargando TWRP recovery...");
        var twrpUrl = $"https://dl.twrp.me/{deviceInfo?.Manufacturer?.ToLower() ?? "generic"}/twrp-*.img";
        Log($"  URL: {twrpUrl}");
        Log("  NOTA: Debes descargar manualmente el recovery correcto.");
        Log("  Opciones:");
        Log("    1. TWRP: https://twrp.me/Devices/");
        Log("    2. OrangeFox: https://orangefox.download/");
        Log("    3. SHRP: https://srp.ml/");

        Log("\nPASO 3: Instrucciones para flashear recovery...");
        Log("  fastboot flash recovery recovery.img");
        Log("  fastboot reboot");
        Log("\nUna vez con recovery instalado:");
        Log("  1. Descarga Magisk ZIP desde: https://github.com/topjohnwu/Magisk/releases");
        Log("  2. Transfiere a la memoria del dispositivo");
        Log("  3. En recovery: Install > Magisk.zip > Swipe to confirm");
        Log("  4. Reboot system");

        Log("\n═══════════════════════════════════════════");
        Log("  ACCIÓN MANUAL REQUERIDA");
        Log("═══════════════════════════════════════════");
        Log("  Este método requiere que realices pasos manuales.");
        Log("  Sigue las instrucciones de arriba para completar el root.");
        Log("  El dispositivo se reinició a fastboot. Puedes:");
        Log("  1. Flashear TWRP ahora si ya lo descargaste");
        Log("  2. Reiniciar normalmente si aún no estás listo");
        Log("");

        _adbService.FastbootReboot(serial);
        _adbService.RestartAdb();
        return RootMethodStatus.WaitingDevice;
    }

    private void InstallMagiskCompanion(string serial, CancellationToken ct)
    {
        Log("  Instalando Magisk companion app...");
        var magiskApk = Path.Combine(_magiskService.MagiskDir, "Magisk.apk");
        if (File.Exists(magiskApk))
        {
            _adbService.InstallApk(serial, magiskApk);
        }
        else
        {
            Log("  Magisk.apk no encontrado. Descárgalo desde: https://github.com/topjohnwu/Magisk/releases");
        }
    }

    private async Task<RootMethodStatus> KernelSURootAsync(string serial, CancellationToken ct)
    {
        Log("═══════════════════════════════════════════");
        Log("     ROOT VÍA KERNELSU (ALTERNATIVA MAGISK)");
        Log("═══════════════════════════════════════════");

        var deviceInfo = await _adbService.GetDeviceInfoAsync(serial);
        if (deviceInfo != null)
        {
            Log($"Dispositivo: {deviceInfo.Manufacturer} {deviceInfo.Model}");
            Log($"Android: {deviceInfo.AndroidVersion} | ABI: {deviceInfo.Abi}");
        }

        Log("\nPASO 1: Verificando bootloader...");
        var blCheck = _adbService.CheckBootloaderUnlock(serial);
        if (AdbService.ParseBootloaderUnlocked(blCheck.Output) != true)
        {
            Log("ERROR: Bootloader debe estar desbloqueado para KernelSU.");
            Log("Usa el método 'Desbloquear Bootloader' primero.");
            return RootMethodStatus.Failed;
        }
        Log("✓ Bootloader desbloqueado.");
        ct.ThrowIfCancellationRequested();

        Log("\nPASO 2: Descargando KernelSU...");
        if (!_magiskService.HasBinaries)
        {
            var dl = await _magiskService.DownloadMagiskAsync();
            if (!dl) return RootMethodStatus.Failed;
        }

        Log("KernelSU usa los mismos binaries que Magisk.");
        Log("El proceso es idéntico al de Magisk Patch.");
        Log("KernelSU es más ligero y no requiere Magisk Manager.");

        return await MagiskRootAsync(serial, ct);
    }

private async Task<RootMethodStatus> OneClickRootAsync(string serial, CancellationToken ct)
    {
        Log("╔═══════════════════════════════════════════╗");
        Log("║     ROOT AUTOMÁTICO (sin borrar datos)    ║");
        Log("╚═══════════════════════════════════════════╝");
        Log("No desbloquea el bootloader. Si está cerrado, se detiene");
        Log("y el teléfono queda como estaba.");
        Log("");

        // ── FASE 0: Verificar ADB ──
        Log("▸ Verificando conexión ADB...");
        if (!_adbService.CheckAdbHealthy())
        {
            LogError("ADB no responde. Reiniciando servidor...");
            _adbService.RestartAdb();
            await Task.Delay(2000, ct);
            if (!_adbService.CheckAdbHealthy())
            {
                LogError("ADB no disponible. Verifica drivers USB y depuración USB.");
                return RootMethodStatus.Failed;
            }
        }
        LogOk("ADB operativo.");
        ct.ThrowIfCancellationRequested();

        // ── FASE 1: Detectar dispositivo ──
        Log("\n▸ FASE 1: Detectando dispositivo...");
        var deviceInfo = await _adbService.GetDeviceInfoAsync(serial);
        if (deviceInfo == null)
        {
            LogError("No se pudo obtener información del dispositivo.");
            Log("  Asegúrate de que:");
            Log("  1. El dispositivo está conectado por USB");
            Log("  2. Depuración USB está activada");
            Log("  3. Aceptaste la solicitud RSA en la pantalla del teléfono");
            return RootMethodStatus.Failed;
        }

        var model = deviceInfo.Model;
        var androidVersion = deviceInfo.AndroidVersion;
        var abi = deviceInfo.Abi;

        LogOk($"Dispositivo: {deviceInfo.Manufacturer} {model}");
        LogOk($"Android: {androidVersion} | ABI: {abi}");
        LogOk($"Batería: {deviceInfo.BatteryLevel}");

        // Verificar batería mínima (fail-closed: sin lectura fiable no se procede)
        if (!await EnsureMinBatteryAsync(serial, deviceInfo))
            return RootMethodStatus.Failed;
        ct.ThrowIfCancellationRequested();

        // ── FASE 2: Verificar si ya tiene root ──
        Log("\n▸ FASE 2: Verificando root actual...");
        var suNow = _adbService.ExecuteAdb($"-s {serial} shell su -c id", ct: ct, timeoutMs: 8000);
        if (suNow.Success && RootSafetyPolicy.OutputShowsRootUid(suNow.Output))
        {
            LogOk("═══════════════════════════════════════════");
            LogOk("  EL DISPOSITIVO YA TIENE ROOT (uid=0)");
            LogOk("═══════════════════════════════════════════");
            Log("  No se toca el arranque.");
            return RootMethodStatus.Success;
        }
        Log("  Dispositivo sin root. Procediendo...");
        ct.ThrowIfCancellationRequested();

        // ── FASE 3: Magisk. Si el bootloader está cerrado, Magisk lo desbloquea antes. ──
        Log("\n▸ FASE 3: Bootloader y Magisk...");
        Log("  Cerrado confirmado: backup, desbloqueo (borra el teléfono) y después el parche.");
        Log("  Ya abierto: solo el parche, sin formatear.");
        Log("  Estado desconocido: no se hace nada.");
        ct.ThrowIfCancellationRequested();

        Log("");
        Log("▸ Aplicando root via Magisk...");
        Log("  1. Desbloquea el bootloader si está cerrado");
        Log("  2. Extrae init_boot o boot y lo parchea");
        Log("  3. Si es boot, lo prueba en RAM y solo graba si hay uid=0");
        Log("  4. Si es init_boot, graba esa partición");
        Log("");

        var magiskResult = await MagiskRootAsync(serial, ct);
        if (magiskResult == RootMethodStatus.Success)
        {
            Log("");
            Log("╔═══════════════════════════════════════════╗");
            Log("║     ✅ ROOT COMPLETADO EXITOSAMENTE       ║");
            Log("╚═══════════════════════════════════════════╝");
            Log("");
            Log("  Después de reiniciar:");
            Log("  1. Busca la app 'Magisk' en el cajón de aplicaciones");
            Log("  2. Ábrela para completar la configuración inicial");
            Log("  3. Si Magisk no aparece, descárgala desde:");
            Log("     https://github.com/topjohnwu/Magisk/releases");
            return RootMethodStatus.Success;
        }

        LogError("El root via Magisk Patch no se completó.");
        Log("  No se intentan métodos experimentales: no rootearían y pueden reiniciar el teléfono.");
        Log("  Los datos de usuario no se han borrado.");
        Log($"  Dispositivo: {deviceInfo.Manufacturer} {model}");
        Log($"  Android: {androidVersion}");
        return magiskResult is RootMethodStatus.WaitingDevice or RootMethodStatus.NotSupported
            ? magiskResult
            : RootMethodStatus.Failed;
    }

    /// <summary>
    /// Carpeta nueva por intento. Reutilizar backup/{serial} hacía que archivos
    /// de un intento viejo contaran como backup de hoy y autorizaran el wipe.
    /// </summary>
    private static string CreateFreshBackupDirectory(string serial)
    {
        var dir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "backup",
            AdbService.SanitizeSerialForPath(serial),
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task<string?> CreateFullBackupAsync(string serial, CancellationToken ct)
    {
        try
        {
            var backupDir = CreateFreshBackupDirectory(serial);

            Log("  [1/4] Backup de apps...");
            ct.ThrowIfCancellationRequested();
            var apps = await BackupAppsAsync(serial, backupDir, ct);
            Log($"    → {apps.Count} apps respaldadas");

            Log("  [2/4] Backup de archivos multimedia...");
            ct.ThrowIfCancellationRequested();
            var media = await BackupMediaAsync(serial, backupDir, ct);
            Log($"    → {media.Count} archivos multimedia");

            Log("  [3/4] Backup de documentos...");
            ct.ThrowIfCancellationRequested();
            var docs = await BackupDocumentsAsync(serial, backupDir, ct);
            Log($"    → {docs.Count} documentos");

            Log("  [4/4] Backup de contactos y SMS...");
            ct.ThrowIfCancellationRequested();
            await BackupContactsSmsAsync(serial, backupDir, ct);

            // Gate: sin apps/fotos/documentos de ESTE intento, el backup no es válido.
            if (!RootSafetyPolicy.BackupHasUserFiles(apps.Count, media.Count, docs.Count))
            {
                LogError("Backup sin apps, fotos ni documentos — no se considera válido.");
                return null;
            }
            var fileCount = 0;
            try
            {
                fileCount = Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories).Length;
            }
            catch
            {
                fileCount = 0;
            }

            // Crear resumen JSON
            var summary = new
            {
                backupDir,
                serial,
                timestamp = DateTime.Now,
                appsCount = apps.Count,
                mediaCount = media.Count,
                documentsCount = docs.Count,
                totalFiles = fileCount
            };
            var summaryPath = Path.Combine(backupDir, "backup_summary.json");
            await File.WriteAllTextAsync(summaryPath,
                System.Text.Json.JsonSerializer.Serialize(summary, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), ct);

            // Comprimir a ZIP
            try
            {
                var zipPath = backupDir + ".zip";
                if (File.Exists(zipPath)) File.Delete(zipPath);
                Log("  Comprimiendo backup...");
                ZipFile.CreateFromDirectory(backupDir, zipPath, CompressionLevel.Optimal, false);
                LogOk($"  Backup comprimido: {new FileInfo(zipPath).Length / 1024 / 1024} MB");
            }
            catch (Exception ex)
            {
                LogWarning($"  No se pudo comprimir: {ex.Message}");
            }

            return backupDir;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogWarning($"  Error creando backup: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> WaitForDeviceReadyAsync(string serial, CancellationToken ct, int maxWaitSeconds = 300)
    {
        Log("  Esperando a que el dispositivo se reinicie...");
        Log("  (puede tardar hasta 5 minutos después del formateo)");

        _adbService.RestartAdb();

        for (int i = 0; i < maxWaitSeconds; i++)
        {
            if (ct.IsCancellationRequested)
                return false;

            await Task.Delay(1000, ct);

            if (i % 15 == 0 && i > 0)
                Log($"    Esperando... ({i}s / {maxWaitSeconds}s)");

            var devices = await Task.Run(() => _adbService.GetConnectedDevices(), ct);
            if (devices.Contains(serial))
            {
                var check = await Task.Run(() =>
                    _adbService.ExecuteAdb($"-s {serial} shell getprop sys.boot_completed", ct: ct, timeoutMs: 5000), ct);
                if (check.Success && check.Output.Trim() == "1")
                {
                    LogOk("Dispositivo listo y respondiendo.");
                    return true;
                }
            }
        }

        LogWarning("Tiempo de espera agotado. El dispositivo puede estar aún reiniciándose.");
        Log("  Si el dispositivo no aparece, reconecta el cable USB y pulsa Refresh.");
        return false;
    }

    private async Task<RootMethodStatus> SamsungUnlockFlowAsync(string serial, DeviceInfo deviceInfo, CancellationToken ct)
    {
        Log("╔═══════════════════════════════════════════╗");
        Log("║   SAMSUNG — Desbloqueo Bootloader Manual  ║");
        Log("╚═══════════════════════════════════════════╝");
        Log("");

        // Verificar si One UI 8 deshabilitó OEM toggle
        var oemLocked = _adbService.ExecuteAdb($"-s {serial} shell getprop ro.boot.other.locked");
        if (oemLocked.Success && oemLocked.Output.Trim() == "1")
        {
            LogWarning("One UI 8 detectado — el toggle 'Desbloqueo OEM' puede no estar disponible.");
            if (deviceInfo.IsMediaTek)
            {
                LogOk("Tu dispositivo usa chip MediaTek → compatible con MTKClient.");
                Log("  Usa 'MTKClient Unlock' de la lista como alternativa.");
                Log("");
            }
        }

        // Backup
        Log("▸ Creando backup antes del desbloqueo...");
        var backupDir = await CreateFullBackupAsync(serial, ct);
        if (backupDir == null)
        {
            LogWarning("⚠ No se pudo crear backup automático.");
            Log("  ANTES de desbloquear, copia tus datos manualmente (el wipe es permanente).");
        }

        Log("");
        Log("═══════════════════════════════════════════");
        Log("  INSTRUCCIONES PARA DESBLOQUEAR SAMSUNG");
        Log("═══════════════════════════════════════════");
        Log("");
        Log("  Paso 1: En el teléfono ve a:");
        Log("    Ajustes → Acerca del teléfono → Información de software");
        Log("    → Toca 'Número de compilación' 7 veces para activar Dev Options");
        Log("");
        Log("  Paso 2: Ve a:");
        Log("    Ajustes → Opciones de desarrollador");
        Log("    → Activa 'Desbloqueo OEM'");
        Log("");
        Log("  Paso 3: Apaga el teléfono completamente");
        Log("");
        Log("  Paso 4: Entra en Download Mode:");
        Log("    • Con el teléfono apagado");
        Log("    • Mantén VOL- + VOL+ juntos");
        Log("    • SIN SOLTAR, conecta el cable USB al PC");
        Log("    • Suelta cuando veas pantalla de advertencia");
        Log("");
        Log("  Paso 5: En la pantalla de advertencia:");
        Log("    • Pulsa VOL+ LARGO para confirmar desbloqueo");
        Log("    • El teléfono se borrará y reiniciará solo");
        Log("");
        LogWarning("Knox se dispara permanentemente:");
        Log("  Samsung Pay, Secure Folder y otros servicios Knox");
        Log("  deixarán de funcionar de forma PERMANENTE.");
        Log("");
        Log("  Paso 6: Después del reinicio:");
        Log("    1. Configura el teléfono de nuevo");
        Log("    2. Activa Depuración USB");
        Log("    3. Conecta al PC");
        Log("    4. Vuelve a ejecutar 'One-Click Root'");
        Log("");
        if (backupDir != null)
            Log($"  Backup guardado en: {backupDir}");
        Log("");
        Log("  Cuando hayas completado estos pasos, pulsa OK.");
        Log("  (La aplicación esperará a que reconectes el dispositivo)");

        _adbService.RestartAdb();
        return RootMethodStatus.WaitingDevice;
    }

    private async Task<RootMethodStatus> TemporaryRootAsync(string serial, CancellationToken ct)
    {
        Log("═══════════════════════════════════════════");
        Log("     ROOT TEMPORAL (Sin desbloquear bootloader)");
        Log("═══════════════════════════════════════════");
        Log("⚠ IMPORTANTE: Android 8.0+ NO permite root sin desbloquear bootloader");
        Log("Google parcheó todos los exploits después de Dirty COW");

        var deviceInfo = await _adbService.GetDeviceInfoAsync(serial);
        if (deviceInfo != null)
        {
            Log($"Dispositivo: {deviceInfo.Manufacturer} {deviceInfo.Model}");
            Log($"Android: {deviceInfo.AndroidVersion}");

            var androidVer = deviceInfo.AndroidVersion;
            var major = 0;
            var verPart = androidVer.Split('.')[0];
            int.TryParse(verPart, out major);
            // Android 8+ (Oreo, Pie, 10, 11, …) needs unlocked bootloader
            if (major >= 8)
            {
                Log("\n❌ Android 8.0+ requiere desbloquear bootloader para root.");
                Log("   El desbloqueo BORRA todos los datos del dispositivo.");
                Log("   Usa 'Desbloquear Bootloader' primero, luego 'Magisk Patch'.");
                return RootMethodStatus.NotSupported;
            }
        }

        Log("\nPASO 1: Intentando métodos temporales...");

        Log("  [1/4] Verificando SELinux...");
        var selinux = _adbService.Shell(serial, "getenforce", ct: ct);
        Log($"  SELinux: {selinux.Output}");

        Log("  [2/4] Intentando disable verity...");
        var verity = _adbService.Shell(serial, "disable-verity 2>/dev/null || echo 'failed'", ct: ct);
        Log($"  Disable-verity: {verity.Output}");

        Log("  [3/4] Intentando mount rw...");
        var mount = _adbService.Shell(serial, "mount -o rw,remount /system 2>&1", ct: ct);
        if (mount.Success) Log("  ✓ /system montado como RW");

        Log("  [4/4] Buscando vulnerabilidades en proc...");
        var pid = FindExploitablePid(serial);
        if (pid > 0)
        {
            Log($"  ✓ PID potencial: {pid}");
            return ExploitPid(serial, pid);
        }

        Log("\n═══════════════════════════════════════════");
        Log("  RESULTADO: Root temporal no disponible");
        Log("═══════════════════════════════════════════");
        Log("  El exploit temporal no está implementado en esta versión.");
        Log("  Necesitas desbloquear bootloader para root persistente.");

        return RootMethodStatus.NotSupported;
    }

    private async Task<RootMethodStatus> FastbootBootAsync(string serial, CancellationToken ct)
    {
        Log("═══════════════════════════════════════════");
        Log("     ROOT TEMPORAL VÍA FASTBOOT BOOT");
        Log("═══════════════════════════════════════════");
        Log("  Este método NO flashea la partición boot.");
        Log("  Si algo sale mal, solo reinicia y vuelve a Android normal.");
        Log("  ⚠ El root se pierde al reiniciar el dispositivo.");

        var deviceInfo = await _adbService.GetDeviceInfoAsync(serial);
        if (deviceInfo == null)
        {
            Log("ERROR: No se pudo obtener información del dispositivo.");
            return RootMethodStatus.Failed;
        }

        Log($"Dispositivo: {deviceInfo.Manufacturer} {deviceInfo.Model}");
        Log($"Android: {deviceInfo.AndroidVersion} | ABI: {deviceInfo.Abi}");

        // Gate de batería antes del parche/arranque — fail-closed
        if (!await EnsureMinBatteryAsync(serial, deviceInfo))
            return RootMethodStatus.Failed;

        var blFastboot = _adbService.CheckBootloaderUnlock(serial);
        if (AdbService.ParseBootloaderUnlocked(blFastboot.Output) != true)
        {
            LogError("Bootloader bloqueado o no confirmado. No se reinicia el teléfono.");
            Log("  fastboot boot no rootearía, y desbloquear el bootloader BORRA los datos.");
            return RootMethodStatus.Failed;
        }

        ct.ThrowIfCancellationRequested();

        Log("\nPASO 1: Buscando partición boot...");
        var bootPart = await _adbService.FindBootPartitionAsync(serial, ct);
        if (bootPart == null)
        {
            Log("ERROR: No se encontró la partición boot.");
            return RootMethodStatus.Failed;
        }
        Log($"Partición boot encontrada: {bootPart.Path} ({bootPart.Size / 1024 / 1024} MB)");
        if (RootSafetyPolicy.IsInitBootPartition(bootPart.FastbootPartition.Length > 0 ? bootPart.FastbootPartition : bootPart.Path))
        {
            LogError("Este teléfono usa init_boot. fastboot boot no puede probarlo y no se va a grabar desde aquí.");
            Log("  Usa 'Root con Magisk': pedirá confirmación antes de grabar solo init_boot.");
            Log("  No se ha reiniciado el teléfono.");
            return RootMethodStatus.Failed;
        }

        ct.ThrowIfCancellationRequested();

        Log("\nPASO 2: Descargando Magisk...");
        if (!_magiskService.HasBinaries)
        {
            var downloaded = await _magiskService.DownloadMagiskAsync();
            if (!downloaded)
            {
                Log("ERROR: No se pudo descargar Magisk.");
                return RootMethodStatus.Failed;
            }
        }
        Log("Magisk listo.");

        ct.ThrowIfCancellationRequested();

        Log("\nPASO 3: Extrayendo boot.img del dispositivo...");
        var bootImgLocal = Path.Combine(_magiskService.MagiskDir, "boot.img");
        var extracted = await _adbService.ExtractBootImgAsync(serial, bootPart, bootImgLocal, ct);
        if (!extracted)
        {
            Log("ERROR: No se pudo extraer boot.img.");
            return RootMethodStatus.Failed;
        }

        ct.ThrowIfCancellationRequested();

        Log("\nPASO 4: Preparando binaries de Magisk en el dispositivo...");
        var remoteDir = RemoteMagiskDir;
        var pushed = PushMagiskBinaries(serial, deviceInfo.Abi, ct);
        if (pushed == 0)
        {
            LogWarning("No se pudieron subir todos los binaries de Magisk.");
        }

        ct.ThrowIfCancellationRequested();

        Log("\nPASO 5: Subiendo boot.img al dispositivo...");
        var remoteBoot = "/data/local/tmp/boot_to_patch.img";
        var pushResult = _adbService.PushFile(serial, bootImgLocal, remoteBoot);
        if (!pushResult.Success)
        {
            Log("ERROR: No se pudo subir boot.img al dispositivo.");
            return RootMethodStatus.Failed;
        }

        ct.ThrowIfCancellationRequested();

        Log("\nPASO 6: Parcheando boot.img con magiskboot...");
        var magiskBoot = RemoteMagiskBootPath(deviceInfo.Abi);

        var unpackCmd = $"cd /data/local/tmp && {magiskBoot} unpack boot_to_patch.img 2>/dev/null";
        // Limpiar restos de runs anteriores (new-boot.img stale podría superar el check de tamaño)
        _adbService.Shell(serial, "rm -f /data/local/tmp/new-boot.img /data/local/tmp/ramdisk.cpio /data/local/tmp/boot_to_patch.img.bak");
        var unpackResult = _adbService.Shell(serial, unpackCmd);
        if (!unpackResult.Success)
        {
            Log("  Error al desempaquetar, intentando método alternativo...");
            unpackResult = _adbService.Shell(serial, $"cd /data/local/tmp && {magiskBoot} unpack -h boot_to_patch.img");
            if (!unpackResult.Success)
            {
                Log("ERROR: No se pudo desempaquetar boot.img.");
                return RootMethodStatus.Failed;
            }
        }
        Log("  boot.img desempaquetado.");

        ct.ThrowIfCancellationRequested();

        Log("  Aplicando parche Magisk al ramdisk...");
        var fastbootIs64 = deviceInfo.Abi.Contains("64", StringComparison.OrdinalIgnoreCase);
        var magiskBinName = fastbootIs64 ? "magisk64" : "magisk32";
        var magiskBinPath = $"{remoteDir}/{LocalMagiskBinPath(magiskBinName, deviceInfo.Abi)}";
        var magiskInitRemote = $"{remoteDir}/{LocalMagiskBinPath("magiskinit", deviceInfo.Abi)}";
        var ramdiskPatchCmd = $"cd /data/local/tmp && {magiskBoot} cpio ramdisk.cpio 'mkdir 0750 overlay.d' " +
                              $"&& {magiskBoot} cpio ramdisk.cpio 'mkdir 0750 overlay.d/sbin' " +
                              $"&& {magiskBoot} cpio ramdisk.cpio 'add 0750 overlay.d/sbin/magisk {magiskBinPath}' " +
                              $"&& {magiskBoot} cpio ramdisk.cpio 'add 0644 overlay.d/sbin/magiskinit {magiskInitRemote}'";

        var patchResult = _adbService.Shell(serial, ramdiskPatchCmd);
        if (!patchResult.Success)
        {
            Log("  Error en parche completo, intentando parche simple...");
            var simplePatch = $"cd /data/local/tmp && {magiskBoot} cpio ramdisk.cpio 'add 0750 sbin/magisk {magiskBinPath}'";
            _adbService.Shell(serial, simplePatch);
        }
        Log("  Ramdisk parcheado.");

        ct.ThrowIfCancellationRequested();

        Log("  Reempaquetando boot.img...");
        var repackCmd = $"cd /data/local/tmp && {magiskBoot} repack boot_to_patch.img 2>/dev/null";
        var repackResult = _adbService.Shell(serial, repackCmd);
        if (!repackResult.Success)
        {
            repackResult = _adbService.Shell(serial, $"cd /data/local/tmp && {magiskBoot} repack -n boot_to_patch.img new-boot.img 2>/dev/null");
        }

        var repackCheck = _adbService.Shell(serial, "wc -c < /data/local/tmp/new-boot.img 2>/dev/null");
        var repackedSize = 0L;
        long.TryParse(repackCheck.Output.Trim(), out repackedSize);
        if (repackedSize < 100000)
        {
            Log($"ERROR: boot.img reempaquetado inválido ({repackedSize} bytes).");
            return RootMethodStatus.Failed;
        }
        Log($"  boot.img reempaquetado ({repackedSize / 1024 / 1024} MB)");

        ct.ThrowIfCancellationRequested();

        Log("\nPASO 7: Descargando boot.img parcheado...");
        var patchedImgLocal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "magisk_patched_boot.img");
        var pulled = await _adbService.ExecuteAdbRawAsync(serial, "/data/local/tmp/new-boot.img", patchedImgLocal, ct);
        if (!pulled)
        {
            Log("  Intentando pull normal...");
            var pullResult = _adbService.PullFile(serial, "/data/local/tmp/new-boot.img", patchedImgLocal);
            pulled = pullResult.Success && File.Exists(patchedImgLocal) && new FileInfo(patchedImgLocal).Length > 100000;
        }
        if (!pulled)
        {
            Log("ERROR: No se pudo descargar el boot.img parcheado.");
            return RootMethodStatus.Failed;
        }
        Log($"  boot.img parcheado: {patchedImgLocal}");

        // Validación anti-brick: no arrancar con un binario sin verificar
        var fastbootPatchedValidation = BootImageValidator.ValidatePatched(patchedImgLocal, bootImgLocal);
        if (fastbootPatchedValidation.Status != BootImageValidator.ValidationStatus.Valid)
        {
            LogError($"VALIDACIÓN FALLIDA: {fastbootPatchedValidation.Message}");
            Log("  No se usará un boot.img que no pasa las verificaciones de seguridad.");
            Log($"  Original (si existe): {bootImgLocal}");
            return RootMethodStatus.Failed;
        }
        LogOk($"  boot.img parcheado válido ({fastbootPatchedValidation.FileSize / 1024 / 1024} MB)");

        ct.ThrowIfCancellationRequested();

        Log("\nPASO 8: Arrancando con boot.img parcheado vía fastboot boot...");
        Log("  ⚠ NO FLASHEA la partición. Si falla, reinicia el dispositivo.");
        Log("  Reiniciando a bootloader...");
        _adbService.RebootToBootloader(serial);

        var fastbootReady = _adbService.WaitForFastbootDevice(serial, 30, ct);
        if (!fastbootReady)
        {
            Log("ERROR: No se detecta el dispositivo en modo fastboot.");
            Log("  El boot.img parcheado se guardó en: " + patchedImgLocal);
            Log("  Puedes intentar manualmente: fastboot boot " + patchedImgLocal);
            return RootMethodStatus.Failed;
        }
        Log("  Dispositivo en modo fastboot.");

        ct.ThrowIfCancellationRequested();

        Log("\n  Ejecutando: fastboot boot " + patchedImgLocal);
        Log("  ⚠ REVISA LA PANTALLA DEL TELÉFONO");
        Log("  El dispositivo arrancará temporalmente con Magisk.");
        Log("  Si se queda en el logo o no arranca, mantén presionado");
        Log("  el botón de encendido 10s para reiniciar.");

        var fbTarget = _adbService.ResolveFastbootTarget(serial);
        var bootResult = _adbService.ExecuteFastboot($"-s {fbTarget} boot \"{patchedImgLocal}\"", timeoutMs: 60000);
        // Sin fallback "sin serial": con varios dispositivos conectados podría
        // arrancar el equipo equivocado (ResolveFastbootTarget ya cubre "?").

        if (bootResult.Success)
        {
            Log("  fastboot boot aceptado. Esperando arranque y verificando root...");
            _adbService.RestartAdb();

            // Verificar root REAL antes de reportar éxito (H8)
            bool tempRootVerified = false;
            for (int i = 0; i < 90 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(1000, ct);
                var devs = _adbService.GetConnectedDevices();
                if (!devs.Contains(serial)) continue;

                var bootCheck = _adbService.ExecuteAdb(
                    $"-s {serial} shell getprop sys.boot_completed", ct: ct, timeoutMs: 5000);
                if (!bootCheck.Success || bootCheck.Output.Trim() != "1") continue;

                // Magisk puede tardar unos segundos en levantar su daemon
                for (int attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
                {
                    if (DetectIfRooted(serial))
                    {
                        tempRootVerified = true;
                        break;
                    }
                    await Task.Delay(5000, ct);
                }
                break;
            }

            if (!tempRootVerified)
            {
                LogWarning("El dispositivo arrancó pero el root temporal NO se verificó.");
                Log("  Revisa la pantalla del teléfono y la app Magisk si aparece.");
                Log("  Usa 'Magisk Patch' para un root permanente.");
                return RootMethodStatus.Failed;
            }

            Log("═══════════════════════════════════════════");
            Log("  ✅ ROOT TEMPORAL INICIADO Y VERIFICADO");
            Log("═══════════════════════════════════════════");
            Log("  El dispositivo arrancó con Magisk temporal.");
            Log("  ⚠ El root se pierde al reiniciar.");
            Log("  Si todo funciona bien, usa 'Magisk Patch' para hacerlo permanente.");
            return RootMethodStatus.Success;
        }

        _adbService.RestartAdb();
        Log("\nERROR: No se pudo arrancar con el boot.img parcheado.");
        Log("  Causas posibles:");
        Log("  • El boot.img parcheado es incompatible con el kernel");
        Log("  • El bootloader está bloqueado (fastboot boot requiere bootloader desbloqueado)");
        Log("  • La partición boot no es booteable directamente");
        Log("\n  No se ha grabado nada. No hace falta desbloquear el bootloader otra vez.");
        return RootMethodStatus.Failed;
    }

    public bool PendingIsInitBoot { get; private set; }

    public async Task<RootMethodStatus> CommitPatchedBootAsync(string serial, CancellationToken ct)
    {
        var session = LoadBootSession(serial);
        if (session == null)
        {
            LogError("No hay un parche pendiente de grabar.");
            return RootMethodStatus.Failed;
        }

        var unlocked = AdbService.ParseBootloaderUnlocked(_adbService.CheckBootloaderUnlock(serial).Output);
        var originalValid = File.Exists(session.OriginalPath)
            && BootImageValidator.ValidateOriginal(session.OriginalPath).Status == BootImageValidator.ValidationStatus.Valid;
        var patchedOk = File.Exists(session.PatchedPath)
            && BootImageValidator.ValidatePatched(session.PatchedPath, session.OriginalPath).Status == BootImageValidator.ValidationStatus.Valid;

        if (!RootSafetyPolicy.MayFlashPermanent(
                unlocked, originalValid && patchedOk, session.PatchEvidence, userConfirmed: true,
                session.TempBootVerified, session.IsInitBoot, session.UseDownloadMode)
            || !RootSafetyPolicy.IsFlashableBootPartition(session.FastbootPartition))
        {
            LogError("No se cumplen las condiciones para grabar. No se flashea.");
            Log("  Hace falta bootloader confirmado, copia original, parche válido y tu confirmación.");
            return RootMethodStatus.Failed;
        }

        if (!await EnsureMinBatteryAsync(serial))
            return RootMethodStatus.Failed;
        ct.ThrowIfCancellationRequested();

        if (session.UseDownloadMode)
            return await FlashSamsungDownloadAsync(serial, session, ct);

        Log($"Grabando solo {session.FastbootPartition}. No se toca userdata.");
        _adbService.RebootToBootloader(serial);
        if (!_adbService.WaitForFastbootDevice(serial, 45, ct))
        {
            LogError("No entró en fastboot. No se ha flasheado.");
            _adbService.RestartAdb();
            return RootMethodStatus.Failed;
        }

        var flashed = _adbService.FlashBootViaFastboot(serial, session.PatchedPath, session.FastbootPartition);
        if (!flashed)
        {
            LogError("El flash falló. No se reintenta.");
            Log($"  Copia original: {session.OriginalPath}");
            Log("  Usa 'Restaurar boot original' si el teléfono no arranca.");
            _adbService.FastbootReboot(serial);
            _adbService.RestartAdb();
            return RootMethodStatus.Failed;
        }

        LogOk($"{session.FastbootPartition} grabado. Reiniciando...");
        _adbService.FastbootReboot(serial);
        _adbService.RestartAdb();
        var verified = await WaitForRealRootAsync(serial, ct, 120);
        if (!verified)
        {
            LogError("La partición se grabó, pero no se verificó uid=0.");
            Log($"  Restaura el original ({session.OriginalPath}) con el botón de la ventana.");
            return RootMethodStatus.Failed;
        }

        LogOk("Root grabado y verificado. Los datos de usuario no se han borrado.");
        Log($"  Para volver atrás: Restaurar boot original ({session.OriginalPath}).");
        _adbService.Shell(serial, "rm -rf /data/local/tmp/magisk /data/local/tmp/boot_to_patch.img /data/local/tmp/ramdisk.cpio /data/local/tmp/new-boot.img");
        return RootMethodStatus.Success;
    }

    public Task<RootMethodStatus> DiscardPendingBootAsync(string serial, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Log("No se graba nada. Reiniciando al sistema que ya estaba instalado.");
        RebootBackToInstalledSystem(serial);
        var session = LoadBootSession(serial);
        if (session != null)
        {
            session.TempBootVerified = false;
            SaveBootSession(session);
        }
        return Task.FromResult(RootMethodStatus.Failed);
    }

    public async Task<RootMethodStatus> RestoreOriginalBootAsync(string serial, CancellationToken ct)
    {
        var session = LoadBootSession(serial);
        if (session == null || string.IsNullOrEmpty(session.OriginalPath))
        {
            LogError("No hay una copia del boot original para este teléfono.");
            return RootMethodStatus.Failed;
        }

        var unlocked = AdbService.ParseBootloaderUnlocked(_adbService.CheckBootloaderUnlock(serial).Output);
        var originalValid = File.Exists(session.OriginalPath)
            && BootImageValidator.ValidateOriginal(session.OriginalPath).Status == BootImageValidator.ValidationStatus.Valid;
        bool? fingerprintMatches = null;
        if (!string.IsNullOrEmpty(session.Fingerprint) && _adbService.GetConnectedDevices().Contains(serial))
        {
            var current = _adbService.ExecuteAdb($"-s {serial} shell getprop ro.build.fingerprint", ct: ct, timeoutMs: 8000);
            if (current.Success && !string.IsNullOrWhiteSpace(current.Output))
                fingerprintMatches = string.Equals(current.Output.Trim(), session.Fingerprint, StringComparison.Ordinal);
        }

        if (!RootSafetyPolicy.MayRestoreOriginal(unlocked, originalValid, fingerprintMatches)
            || !RootSafetyPolicy.IsFlashableBootPartition(session.FastbootPartition))
        {
            LogError("No se restaura: bootloader no confirmado, copia inválida, partición no permitida o la versión del sistema cambió.");
            Log("  Restaurar un boot de otra versión puede dejar el teléfono sin arrancar.");
            return RootMethodStatus.Failed;
        }

        if (!await EnsureMinBatteryAsync(serial))
            return RootMethodStatus.Failed;

        Log($"Restaurando {session.FastbootPartition} desde {session.OriginalPath}");
        _adbService.RebootToBootloader(serial);
        if (!_adbService.WaitForFastbootDevice(serial, 45, ct))
        {
            LogError("No entró en fastboot. No se ha flasheado.");
            _adbService.RestartAdb();
            return RootMethodStatus.Failed;
        }

        var ok = _adbService.FlashBootViaFastboot(serial, session.OriginalPath, session.FastbootPartition);
        _adbService.FastbootReboot(serial);
        _adbService.RestartAdb();
        if (!ok)
        {
            LogError("No se pudo restaurar el original.");
            return RootMethodStatus.Failed;
        }

        LogOk("Boot original restaurado. No se ha tocado userdata.");
        return RootMethodStatus.Success;
    }

    private async Task<bool> WaitForRealRootAsync(string serial, CancellationToken ct, int maxSeconds)
    {
        for (var i = 0; i < maxSeconds && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(1000, ct);
            if (i % 15 == 0 && i > 0)
                Log($"    Esperando arranque... ({i}s / {maxSeconds}s)");
            if (!_adbService.GetConnectedDevices().Contains(serial))
                continue;
            var boot = _adbService.ExecuteAdb($"-s {serial} shell getprop sys.boot_completed", ct: ct, timeoutMs: 5000);
            if (!boot.Success || boot.Output.Trim() != "1")
                continue;
            await Task.Delay(3000, ct);
            var su = _adbService.ExecuteAdb($"-s {serial} shell su -c id", ct: ct, timeoutMs: 8000);
            if (su.Success && RootSafetyPolicy.OutputShowsRootUid(su.Output))
                return true;
        }
        return false;
    }

    private void RebootBackToInstalledSystem(string serial)
    {
        try
        {
            if (_adbService.GetConnectedDevices().Contains(serial))
                _adbService.RebootDevice(serial);
            else
                _adbService.FastbootReboot(serial);
        }
        catch (Exception ex)
        {
            LogWarning($"No se pudo reiniciar solo: {ex.Message}. Mantén encendido 10 segundos.");
        }
        _adbService.RestartAdb();
    }

    private void SaveBootSession(BootSession session)
    {
        PendingIsInitBoot = session.IsInitBoot;
        var path = BootSessionPath(session.Serial);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(session,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static BootSession? LoadBootSession(string serial)
    {
        var path = BootSessionPath(serial);
        if (!File.Exists(path)) return null;
        return System.Text.Json.JsonSerializer.Deserialize<BootSession>(File.ReadAllText(path));
    }

    private static string BootSessionPath(string serial) =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "boot_sessions",
            AdbService.SanitizeSerialForPath(serial) + ".json");

    private bool DetectIfRooted(string serial)
    {
        // Test 1: which su
        var whichSu = _adbService.ExecuteAdb($"-s {serial} shell which su");
        if (whichSu.Success && !string.IsNullOrWhiteSpace(whichSu.Output) && !whichSu.Output.Contains("not found"))
        {
            // Test 2: su -c id (actual root verification)
            var suId = _adbService.ExecuteAdb($"-s {serial} shell su -c id");
            if (suId.Success && (suId.Output.Contains("uid=0") || suId.Output.Contains("root")))
                return true;
        }

        // Test 3: Magisk Manager installed
        var magiskCheck = _adbService.ExecuteAdb($"-s {serial} shell pm list packages 2>/dev/null | grep -i magisk");
        if (magiskCheck.Success && !string.IsNullOrWhiteSpace(magiskCheck.Output))
        {
            // Verify it's actually a package name (not just an error message)
            var hasPackageName = magiskCheck.Output.Split('\n')
                .Any(l => l.Trim().Replace("package:", "").StartsWith("com.topjohnwu.magisk") ||
                          l.Trim().Replace("package:", "").StartsWith("eu.chainfire.supersu"));
            if (hasPackageName) return true;
        }

        // Test 4: adb root
        var adbRoot = _adbService.ExecuteAdb($"-s {serial} root");
        if (adbRoot.Success && adbRoot.Output.Contains("already running as root"))
            return true;

        return false;
    }

    private int FindExploitablePid(string serial)
    {
        return 0;
    }

    private RootMethodStatus ExploitPid(string serial, int pid)
    {
        Log($"  Intentando exploit para PID {pid}...");
        return RootMethodStatus.Failed;
    }

    private void Log(string message)
    {
        lock (_logLock)
        {
            _logOutput.AppendLine(message);
        }
        LogUpdated?.Invoke(message);
    }

    private void LogOk(string message) => Log($"✅ [OK] {message}");
    private void LogError(string message) => Log($"❌ [ERROR] {message}");
    private void LogWarning(string message) => Log($"⚠ [AVISO] {message}");

    private async Task<RootMethodStatus> MtkClientUnlockAsync(string serial, CancellationToken ct)
    {
        Log("═══════════════════════════════════════════");
        Log("  MTKClient UNLOCK AUTOMÁTICO");
        Log("═══════════════════════════════════════════");
        LogWarning("Esto BORRARÁ TODOS LOS DATOS del dispositivo.");
        LogWarning("Knox se dispara permanentemente.");
        Log("");

        var deviceInfo = await _adbService.GetDeviceInfoAsync(serial);
        Log($"Dispositivo: {deviceInfo?.Manufacturer} {deviceInfo?.Model} (MediaTek)");
        Log($"Android: {deviceInfo?.AndroidVersion}");
        Log("");

        // Gate de batería antes del backup y el unlock MTK (wipe) — fail-closed
        if (!await EnsureMinBatteryAsync(serial, deviceInfo))
            return RootMethodStatus.Failed;

        var mtkDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "mtkclient"));
        var venvPython = Path.Combine(mtkDir, "venv", "Scripts", "python.exe");

        Log($"MTKClient: {mtkDir}");
        if (!Directory.Exists(mtkDir))
        {
            LogError($"No se encuentra la carpeta 'mtkclient' en: {mtkDir}");
            LogError("Clona el repositorio: git clone https://github.com/bkerler/mtkclient.git");
            return RootMethodStatus.Failed;
        }
        if (!File.Exists(venvPython))
        {
            LogError($"Python venv no encontrado en: {venvPython}");
            LogError("Créalo: python -m venv venv && .\\venv\\Scripts\\pip install -e .");
            return RootMethodStatus.Failed;
        }
        LogOk("Entorno MTKClient verificado.");

        Log("\n=== FASE 1: Backup de datos ===");
        var backupDir = CreateFreshBackupDirectory(serial);

        Log("\n[1/4] Backup de apps instaladas...");
        ct.ThrowIfCancellationRequested();
        var mtkApps = await BackupAppsAsync(serial, backupDir, ct);
        Log($"  Apps respaldadas: {mtkApps.Count}");

        Log("\n[2/4] Backup de fotos/vídeos...");
        ct.ThrowIfCancellationRequested();
        var mtkMedia = await BackupMediaAsync(serial, backupDir, ct);
        Log($"  Archivos multimedia: {mtkMedia.Count}");

        Log("\n[3/4] Backup de documentos...");
        ct.ThrowIfCancellationRequested();
        var mtkDocs = await BackupDocumentsAsync(serial, backupDir, ct);
        Log($"  Documentos: {mtkDocs.Count}");

        Log("\n[4/4] Backup de contactos y SMS...");
        ct.ThrowIfCancellationRequested();
        await BackupContactsSmsAsync(serial, backupDir, ct);

        // Gate: no entrar en BROM ni desbloquear si este intento no copió datos reales.
        if (!RootSafetyPolicy.BackupHasUserFiles(mtkApps.Count, mtkMedia.Count, mtkDocs.Count))
        {
            LogError("❌ Backup sin apps, fotos ni documentos — se CANCELA el MTK unlock.");
            Log("  No se ha pasado a modo BROM. Tus datos siguen intactos.");
            Log("  Este backup no salva el teléfono: el desbloqueo lo formatea.");
            return RootMethodStatus.Failed;
        }

        LogWarning($"Backup PARCIAL en: {backupDir}");
        LogWarning("No es una copia completa. El desbloqueo MTK BORRA el teléfono.");

        Log("\n=== FASE 2: Modo BROM ===");
        Log("1. APAGA el teléfono por completo.");
        Log("2. Mantén presionado VOLUMEN +.");
        Log("3. SIN SOLTAR VOL+, conecta el cable USB al PC.");
        Log("4. Suelta VOL+ cuando veas el mensaje de detección.");
        Log("");
        Log("  ⏳ Escaneando dispositivo en modo BROM...");
        Log("");

        bool bromDetected = false;
        for (int i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (IsBromDeviceDetected())
            {
                bromDetected = true;
                LogOk("¡Dispositivo MediaTek en modo BROM detectado!");
                await Task.Delay(1500, ct);
                break;
            }
            if (i % 5 == 0 && i > 0)
                Log($"  Aún esperando... ({i}s)");
            await Task.Delay(1000, ct);
        }

        if (!bromDetected)
        {
            LogWarning("No se detectó modo BROM automáticamente.");
            LogWarning("Posibles soluciones:");
            LogWarning("- Prueba con VOL- en lugar de VOL+");
            LogWarning("- Prueba con ambos volumenes");
            LogWarning("- Cambia de cable USB o puerto");
            LogWarning("- Abre el teléfono y puentea test point (busca 'test point BROM' + tu modelo en YouTube)");
            Log("Igualmente se intentará ejecutar MTKClient por si acaso...");
        }

        Log("\n=== FASE 3: Ejecutando MTKClient ===");
        LogOk("Iniciando desbloqueo vía MTKClient...");
        Log("");

        var psi = new ProcessStartInfo
        {
            FileName = venvPython,
            Arguments = "-m mtkclient da seccfg unlock",
            WorkingDirectory = mtkDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            using var proc = new Process { StartInfo = psi };

            proc.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Log($"  {e.Data}");
            };
            proc.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    LogWarning($"  {e.Data}");
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var procCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            procCts.CancelAfter(TimeSpan.FromSeconds(120));
            try
            {
                await proc.WaitForExitAsync(procCts.Token);
            }
            finally
            {
                // Matar el proceso en timeout/cancelación: un MTKClient huérfano
                // puede dejar el dispositivo colgado en BROM.
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(entireProcessTree: true);
                }
                catch { /* ya terminó o sin permisos */ }
            }

            Log("");

            if (proc.ExitCode == 0)
            {
                LogOk("══════════════════════════════════════");
                LogOk("  BOOTLOADER DESBLOQUEADO CON ÉXITO");
                LogOk("══════════════════════════════════════");
                Log("");
                Log("Ahora:");
                Log("1. Desconecta el cable USB");
                Log("2. Mantén VOL- + VOL+ juntos y conecta USB");
                Log("3. Pulsa VOL+ largo para salir y formatear");
                Log("4. El teléfono se reiniciará solo");
                Log("5. Vuelve a conectar con depuración USB");
                Log("6. Usa 'Magisk Patch' de esta herramienta para rootear");
                Log("");

                _adbService.RestartAdb();
                return RootMethodStatus.Success;
            }
            else
            {
                LogError($"MTKClient falló (código {proc.ExitCode})");
                Log("Posibles causas:");
                Log("- El teléfono no está en modo BROM");
                Log("- Intentar de nuevo con VOL- en lugar de VOL+");
                Log("- Probar ambos volumenes al conectar USB");
                Log("- Cambiar de cable USB o puerto");
                Log("- Si persiste: abrir el teléfono y puentear test point");
                Log("  (busca en YouTube: 'test point BROM' + tu modelo)");
                Log("");
                Log("Comando manual para depurar:");
                Log($"  cd \"{mtkDir}\" && .\\venv\\Scripts\\python -m mtkclient da seccfg unlock");
                return RootMethodStatus.Failed;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log("Operación cancelada por el usuario.");
            return RootMethodStatus.Failed;
        }
        catch (OperationCanceledException)
        {
            LogError("MTKClient tardó demasiado (>120 segundos).");
            Log("Revisa que el teléfono esté en modo BROM e intenta de nuevo.");
            return RootMethodStatus.Failed;
        }
        catch (Exception ex)
        {
            LogError($"Error al ejecutar MTKClient: {ex.Message}");
            return RootMethodStatus.Failed;
        }
    }

    private bool IsBromDeviceDetected()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil",
                Arguments = "/enum-devices /connected",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            return output.Contains("MediaTek", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("Preloader", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("BROM", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("DA USB", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("MTK", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("VCOM", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
