using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCAndroidRooter.Models;

namespace PCAndroidRooter.Services;

/// <summary>
/// Flujo de root vía Magisk: parcheo de boot/init_boot, prueba en RAM con
/// fastboot boot, commit permanente, restauración del boot original y
/// One-Click / fastboot boot. NO contiene unlock de bootloader ni MTKClient:
/// los gates de bootloader/batería se alcanzan solo vía <see cref="IUnlockDanger"/>.
/// Métodos movidos de RootService sin cambios de comportamiento.
/// </summary>
public class MagiskRootService : RootServiceBase
{
    private readonly AdbService _adbService;
    private readonly MagiskService _magiskService;
    private readonly IUnlockDanger _unlock;

    public MagiskRootService(AdbService adbService, MagiskService magiskService, IUnlockDanger unlock)
    {
        _adbService = adbService;
        _magiskService = magiskService;
        _unlock = unlock;
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

    public async Task<RootMethodStatus> MagiskRootAsync(string serial, CancellationToken ct)
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
        if (!await _unlock.EnsureMinBatteryForRootAsync(serial, deviceInfo, ct))
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

        var unlockGate = await _unlock.EnsureBootloaderForRootAsync(serial, ct);
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
        //  si AMBAS fallan, el parche no se aplicó y flashearía un boot sin root.)
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

    public async Task<RootMethodStatus> OneClickRootAsync(string serial, CancellationToken ct)
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
        if (!await _unlock.EnsureMinBatteryForRootAsync(serial, deviceInfo, ct))
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

    public async Task<RootMethodStatus> FastbootBootAsync(string serial, CancellationToken ct)
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
        if (!await _unlock.EnsureMinBatteryForRootAsync(serial, deviceInfo, ct))
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

        if (!await _unlock.EnsureMinBatteryForRootAsync(serial, null, ct))
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

        if (!await _unlock.EnsureMinBatteryForRootAsync(serial, null, ct))
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
}
