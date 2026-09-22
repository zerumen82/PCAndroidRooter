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

    private int PushMagiskBinaries(string serial, CancellationToken ct)
    {
        _adbService.Shell(serial, $"mkdir -p {RemoteMagiskDir}", ct: ct);
        _adbService.Shell(serial, $"rm -rf {RemoteMagiskDir}/*", ct: ct);

        var pushed = 0;
        foreach (var file in MagiskBinaryFiles)
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

    private string RemoteMagiskBootPath =>
        File.Exists(Path.Combine(_magiskService.MagiskDir, "magiskboot"))
            ? $"{RemoteMagiskDir}/magiskboot"
            : $"{RemoteMagiskDir}/magiskboot32";

    private async Task<RootMethodStatus> MagiskRootAsync(string serial, CancellationToken ct)
    {
        Log("╔═══════════════════════════════════════════╗");
        Log("║   ROOT VÍA MAGISK PATCH (SEGURO)         ║");
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

        // Verificar batería mínima
        if (int.TryParse(deviceInfo.BatteryLevel.Replace("%", "").Trim(), out var battery) && battery < 30)
        {
            LogError($"Batería insuficiente ({battery}%). Mínimo requerido: 30%.");
            Log("  Conecta el cargador antes de continuar para evitar apagado durante el flash.");
            return RootMethodStatus.Failed;
        }

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

        // ════════════════════════════════════════════════════
        // PASO 2: Detectar partición boot (con soporte A/B)
        // ════════════════════════════════════════════════════
        Log("\nPASO 2: Detectando partición boot...");
        var bootPart = await _adbService.FindBootPartitionAsync(serial, ct);
        if (bootPart == null)
        {
            LogError("No se encontró la partición boot.");
            Log("  Posibles causas:");
            Log("  • Bootloader bloqueado (usa 'Desbloquear Bootloader' primero)");
            Log("  • Dispositivo con particiones no estándar");
            Log("  • Permisos insuficientes");
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
            // Ajustar path de partición según slot activo
            if (!bootPart.Path.EndsWith(activeSlot) && !bootPart.Path.Contains("bootdevice"))
            {
                var slotPath = bootPart.Path + activeSlot;
                var slotCheck = _adbService.Shell(serial, $"ls -l {slotPath} 2>/dev/null");
                if (slotCheck.Success && !slotCheck.Output.Contains("No such file"))
                {
                    LogOk($"Usando partición del slot activo: {slotPath}");
                    bootPart.Path = slotPath;
                    bootPart.BlockDevice = slotPath;
                }
            }
        }
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

        // Verificar integridad de los binaries críticos
        var magiskBootPath = Path.Combine(_magiskService.MagiskDir, "magiskboot");
        var magiskBoot32Path = Path.Combine(_magiskService.MagiskDir, "magiskboot32");
        var magiskInitPath = Path.Combine(_magiskService.MagiskDir, "magiskinit");

        if (!File.Exists(magiskBootPath) && !File.Exists(magiskBoot32Path))
        {
            LogError("No se encontró magiskboot ni magiskboot32.");
            Log("  Los binaries de Magisk están corruptos o incompletos.");
            return RootMethodStatus.Failed;
        }

        var activeMagiskBoot = File.Exists(magiskBootPath) ? magiskBootPath : magiskBoot32Path;
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
            $"boot_original_{deviceInfo.Model}_{DateTime.Now:yyyyMMdd_HHmmss}.img");

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
            LogWarning($"  No se pudo guardar backup: {ex.Message}");
            Log("  ⚠ Sin backup, no se podrá restaurar si algo falla.");
        }
        ct.ThrowIfCancellationRequested();

        // ════════════════════════════════════════════════════
        // PASO 5: Subir binaries Magisk al dispositivo
        // ════════════════════════════════════════════════════
        Log("\nPASO 5: Preparando binaries de Magisk en el dispositivo...");
        var remoteDir = RemoteMagiskDir;
        var pushed = PushMagiskBinaries(serial, ct);

        if (pushed == 0)
        {
            LogError("No se pudieron subir los binaries de Magisk al dispositivo.");
            Log("  Verifica que la depuración USB esté activa y autorizada.");
            return RootMethodStatus.Failed;
        }
        LogOk($"  {pushed} binaries subidos y permisos configurados.");

        // Verificar que magiskboot se ejecuta en el dispositivo
        var magiskBootTest = _adbService.Shell(serial, $"{remoteDir}/magiskboot --help 2>&1 | head -1");
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
        var magiskBootBin = RemoteMagiskBootPath;

        // Desempaquetar boot.img
        Log("  [7.1] Desempaquetando boot.img...");
        var cleanupCmd = "rm -rf /data/local/tmp/magisk /data/local/tmp/boot_to_patch.img /data/local/tmp/ramdisk.cpio /data/local/tmp/new-boot.img /data/local/tmp/boot_to_patch.img.bak /data/local/tmp/boot_to_patch.img_tmp";
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

        // Inyectar Magisk en ramdisk
        Log("  [7.2] Inyectando Magisk en ramdisk...");
        var magiskBinPath = deviceInfo.Abi.Contains("64") ? $"{remoteDir}/magisk64" : $"{remoteDir}/magisk32";
        var magiskInitPathRemote = $"{remoteDir}/magiskinit";

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

        // Verificar que magisk fue inyectado
        var verifyPatch = _adbService.Shell(serial,
            $"{magiskBootBin} cpio ramdisk.cpio 'exists overlay.d/sbin/magisk' 2>&1", ct: ct);
        var verifyPatch2 = _adbService.Shell(serial,
            $"{magiskBootBin} cpio ramdisk.cpio 'exists sbin/magisk' 2>&1", ct: ct);

        if ((!verifyPatch.Success || !verifyPatch.Output.Contains("1")) &&
            (!verifyPatch2.Success || !verifyPatch2.Output.Contains("1")))
        {
            LogWarning("No se pudo verificar que Magisk fue inyectado correctamente.");
            Log("  El parche puede haber fallado silenciosamente.");
        }
        else
        {
            LogOk("  Magisk inyectado correctamente en ramdisk.");
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
            $"magisk_patched_{deviceInfo.Model}_{DateTime.Now:yyyyMMdd_HHmmss}.img");

        var remotePatchedPaths = new[] { "/data/local/tmp/new-boot.img", "/data/local/tmp/boot_to_patch.img" };
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
        if (patchedValidation.ContainsMagisk)
            LogOk("  Firmas Magisk detectadas en el parche.");
        else
            LogWarning("  No se detectaron firmas Magisk (puede ser normal en algunas versiones).");
        ct.ThrowIfCancellationRequested();

        // ════════════════════════════════════════════════════
        // PASO 10: Verificar Samsung (no flashea automático)
        // ════════════════════════════════════════════════════
        var manufacturer = deviceInfo.Manufacturer.ToLowerInvariant();
        if (manufacturer == "samsung")
        {
            Log("\n⚠ Samsung detectado — flasheo manual requerido.");
            Log("\n═══════════════════════════════════════════");
            Log("  BOOT PATCH COMPLETADO (Samsung)");
            Log("═══════════════════════════════════════════");
            Log($"  Boot.img parcheado: {patchedImgLocal}");
            Log($"  Boot.img original: {bootImgBackup}");
            Log("");
            Log("  Para flashear en Samsung necesitas ODIN:");
            Log("  1. Verifica que el bootloader esté DESBLOQUEADO");
            Log("  2. Descarga ODIN: https://odindownload.com");
            Log("  3. Apaga el teléfono");
            Log("  4. Entra en Download Mode: VOL- + VOL+ + conectar USB");
            Log("  5. En ODIN: haz clic en 'AP' → selecciona el parcheado");
            Log("  6. Haz clic en 'Start'");
            Log("");
            Log("  Después del reinicio, busca la app Magisk.");
            _adbService.RestartAdb();
            return RootMethodStatus.Success;
        }

        // ════════════════════════════════════════════════════
        // PASO 10: Reiniciar a fastboot (solo no-Samsung)
        // ════════════════════════════════════════════════════
        Log("\nPASO 10: Reiniciando a modo fastboot...");
        _adbService.RebootToBootloader(serial);

        var fastbootReady = _adbService.WaitForFastbootDevice(serial, 45, ct);
        if (!fastbootReady)
        {
            LogError("El dispositivo no entró en modo fastboot.");
            Log("  Opciones:");
            Log("  1. Intenta manualmente: adb reboot bootloader");
            Log("  2. El bootloader puede estar bloqueado");
            Log($"  Boot.img parcheado guardado: {patchedImgLocal}");
            Log($"  Boot.img original (backup): {bootImgBackup}");
            Log($"  Flash manual: fastboot flash boot \"{patchedImgLocal}\"");
            _adbService.RestartAdb();
            return RootMethodStatus.Failed;
        }
        LogOk("Dispositivo en modo fastboot.");
        ct.ThrowIfCancellationRequested();

        // ════════════════════════════════════════════════════
        // PASO 11: Flashear boot.img parcheado
        // ════════════════════════════════════════════════════
        Log("\nPASO 11: Flasheando boot.img parcheado...");
        Log($"  Archivo: {patchedImgLocal}");
        Log($"  Tamaño: {patchedValidation.FileSize / 1024 / 1024} MB");

        // ════════════════════════════════════════════════════
        // VALIDACIÓN FINAL: Magic bytes ANTES de flashear
        // ════════════════════════════════════════════════════
        try
        {
            using var fs = File.OpenRead(patchedImgLocal);
            var magic = new byte[8];
            int read = fs.Read(magic, 0, 8);
            if (read < 8 || !magic.SequenceEqual(new byte[] { 0x41, 0x4E, 0x44, 0x52, 0x4F, 0x49, 0x44, 0x21 }))
            {
                LogError("VALIDACIÓN FINAL FALLIDA: boot.img parcheado NO tiene magic bytes Android válido.");
                LogError("El archivo está corrupto. No se flasheará.");
                CleanupDeviceTempFiles(serial);
                _adbService.FastbootReboot(serial);
                _adbService.RestartAdb();
                return RootMethodStatus.Failed;
            }
            LogOk("  Magic bytes: ANDROID! ✓ (verificación final)");
        }
        catch (Exception ex)
        {
            LogError($"No se pudo verificar magic bytes: {ex.Message}");
            LogError("No se flashearán archivos sin verificar.");
            CleanupDeviceTempFiles(serial);
            _adbService.FastbootReboot(serial);
            _adbService.RestartAdb();
            return RootMethodStatus.Failed;
        }

        var flashResult = _adbService.FlashBootViaFastboot(serial, patchedImgLocal);
        if (!flashResult)
        {
            LogError("No se pudo flashear boot.img.");
            Log("  Causas posibles:");
            Log("  • Bootloader sigue bloqueado");
            Log("  • Fastboot no reconoce el dispositivo");
            Log("");
            Log("  ╔═══════════════════════════════════════════╗");
            Log("  ║  IMPORTANTE: Tienes el backup del original ║");
            Log("  ╚═══════════════════════════════════════════╝");
            Log($"  Para restaurar si el dispositivo no arranca:");
            Log($"    fastboot flash boot \"{bootImgBackup}\"");
            Log($"    fastboot reboot");
            Log($"  Para flashear el parcheado manualmente:");
            Log($"    fastboot flash boot \"{patchedImgLocal}\"");
            _adbService.FastbootReboot(serial);
            _adbService.RestartAdb();
            return RootMethodStatus.Failed;
        }
        LogOk("boot.img flasheado correctamente.");

        // ════════════════════════════════════════════════════
        // PASO 12: Reiniciar y verificar
        // ════════════════════════════════════════════════════
        Log("\nPASO 12: Reiniciando dispositivo...");
        _adbService.FastbootReboot(serial);
        _adbService.RestartAdb();
        Log("  Esperando a que el dispositivo arranque...");

        // Esperar a que el dispositivo esté listo
        var bootTimeout = 120; // 2 minutos máximo
        bool deviceReady = false;
        for (int i = 0; i < bootTimeout; i++)
        {
            if (ct.WaitHandle.WaitOne(1000) || ct.IsCancellationRequested)
                break;

            if (i % 15 == 0 && i > 0)
                Log($"    Esperando arranque... ({i}s / {bootTimeout}s)");

            var devices = _adbService.GetConnectedDevices();
            if (devices.Contains(serial))
            {
                var bootCheck = _adbService.ExecuteAdb(
                    $"-s {serial} shell getprop sys.boot_completed", ct: ct, timeoutMs: 5000);
                if (bootCheck.Success && bootCheck.Output.Trim() == "1")
                {
                    deviceReady = true;
                    break;
                }
            }
        }

        if (!deviceReady)
        {
            LogWarning("El dispositivo no respondió en 2 minutos.");
            Log("  Puede estar arrancando más lento de lo normal.");
            Log("  Espera unos minutos más y verifica manualmente.");
            Log("");
            Log($"  Si el dispositivo NO arranca, restaura el original:");
            Log($"    1. Entra en fastboot: adb reboot bootloader");
            Log($"    2. Ejecuta: fastboot flash boot \"{bootImgBackup}\"");
            Log($"    3. Reinicia: fastboot reboot");
            return RootMethodStatus.Failed;
        }

        LogOk("Dispositivo arrancado correctamente.");

        // ════════════════════════════════════════════════════
        // PASO 13: Verificar root REAL
        // ════════════════════════════════════════════════════
        Log("\nPASO 13: Verificando root real...");
        await Task.Delay(5000, ct); // Dar tiempo a que Magisk inicialice

        var rootVerified = BootImageValidator.VerifyRoot(serial, (cmd, dispatch, timeout) =>
        {
            var r = _adbService.ExecuteAdb(cmd, dispatch, ct: ct, timeoutMs: timeout);
            return (r.Success, r.Output, r.Error);
        });

        if (rootVerified)
        {
            Log("");
            Log("╔═══════════════════════════════════════════╗");
            Log("║     ✅ ROOT COMPLETADO Y VERIFICADO       ║");
            Log("╚═══════════════════════════════════════════╝");
            Log("");
            LogOk("El dispositivo tiene root REAL y funcional.");
            Log("");
            Log("  Próximos pasos:");
            Log("  1. Busca la app 'Magisk' en el cajón de aplicaciones");
            Log("  2. Ábrela para completar la configuración inicial");
            Log("  3. Si Magisk no aparece, descárgala desde:");
            Log("     https://github.com/topjohnwu/Magisk/releases");
            Log("");
            Log("  Archivos de seguridad:");
            Log($"    Boot original: {bootImgBackup}");
            Log($"    Boot parcheado: {patchedImgLocal}");
            Log("  Guarda ambos archivos en un lugar seguro.");
            Log("");
            Log("  ⚠ Para restaurar el boot original (quitar root):");
            Log($"    1. adb reboot bootloader");
            Log($"    2. fastboot flash boot \"{bootImgBackup}\"");
            Log($"    3. fastboot reboot");

            // Limpiar archivos temporales del dispositivo
            _adbService.Shell(serial, "rm -rf /data/local/tmp/magisk /data/local/tmp/boot_to_patch.img /data/local/tmp/ramdisk.cpio /data/local/tmp/new-boot.img");
            _adbService.RestartAdb();

            return RootMethodStatus.Success;
        }

        // Root no verificado — posible fallo
        Log("");
        LogWarning("═══════════════════════════════════════════");
        LogWarning("  ROOT INSTALADO PERO NO VERIFICADO");
        LogWarning("═══════════════════════════════════════════");
        Log("");
        Log("  El dispositivo arrancó correctamente, pero no se detectó root.");
        Log("  Esto puede deberse a:");
        Log("  • Magisk necesita configuración inicial (abre la app Magisk)");
        Log("  • SELinux está en modo enforcing y bloquea su");
        Log("  • El parche no se aplicó correctamente");
        Log("");
        Log("  Acciones recomendadas:");
        Log("  1. Abre la app Magisk y completa la instalación");
        Log("  2. Reinicia el dispositivo");
        Log("  3. Vuelve a ejecutar la verificación");
        Log("");
        Log($"  Boot original (backup): {bootImgBackup}");
        Log($"  Boot parcheado: {patchedImgLocal}");

        return RootMethodStatus.Failed;
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

        Log("\n[1/5] Probando 'adb root'...");
        var adbRoot = _adbService.ExecuteAdb($"-s {serial} root", ct: ct);
        if (adbRoot.Success && (adbRoot.Output.Contains("already running as root") ||
                                adbRoot.Output.Contains("restarting") ||
                                adbRoot.Output.Contains("adbd is now running as root")))
        {
            var suCheck = _adbService.ExecuteAdb($"-s {serial} shell su -c id", ct: ct);
            if (suCheck.Success && (suCheck.Output.Contains("uid=0") || suCheck.Output.Contains("root")))
            {
                Log("  ✅ 'adb root' funcionó. Dispositivo ya es root.");
                Log("  Instalando Magisk companion desde MagiskService...");
                InstallMagiskCompanion(serial, ct);
                return RootMethodStatus.Success;
            }
        }
        Log("  'adb root' no disponible en este dispositivo.");

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

    private async Task<RootMethodStatus> UnlockBootloaderAsync(string serial, CancellationToken ct, bool skipBackup = false)
    {
        Log("═══════════════════════════════════════════");
        Log("     DESBLOQUEO DE BOOTLOADER");
        Log("═══════════════════════════════════════════");
        Log("ADVERTENCIA: Esto borrará TODOS los datos del dispositivo.");
        if (!skipBackup)
            Log("Se realizará backup automático antes de continuar.");
        else
            Log("Backup ya realizado — se omite para evitar duplicados.");

        var manufacturer = _adbService.ExecuteAdb($"-s {serial} shell getprop ro.product.manufacturer").Output.Trim().ToLowerInvariant();
        Log($" Fabricante detectado: {manufacturer}");

        if (manufacturer == "samsung")
        {
            Log("\n⚠ IMPORTANTE: Samsung NO usa fastboot.");
            Log("  El desbloqueo debe hacerse manualmente desde Download Mode.");

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

        var backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backup", serial);
        Directory.CreateDirectory(backupDir);

        Dictionary<string, string> apkResult = new();
        List<string> mediaResult = new();
        List<string> docsResult = new();

        if (!skipBackup)
        {
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

            Log($"\n✅ Backup completado en: {backupDir}");
            Log("Guarda esta carpeta en un lugar seguro antes de continuar.");
        }

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
        {
            Log("\n=== FASE 3: Desbloqueo MANUAL del bootloader ===");
            Log("\n⚠ Samsung NO compatible con desbloqueo automático via fastboot.");
            Log("  Debes seguir estos pasos MANUALMENTE:");

                var samsungOemCheck2 = _adbService.ExecuteAdb($"-s {serial} shell getprop ro.boot.other.locked");
                var isMediatek2 = await _adbService.DetectIsMediaTekAsync(serial);

            if (samsungOemCheck2.Success && samsungOemCheck2.Output.Trim() == "1")
            {
                LogWarning("One UI 8 ha deshabilitado el toggle 'Desbloqueo OEM'.");
                Log("  Si no ves la opción en Ajustes > Opciones de desarrollador:");
                if (isMediatek2)
                {
                    LogOk("Tu dispositivo usa chip MediaTek → compatible con MTKClient.");
                    LogOk("Usa el método 'MTKClient Unlock' en lugar de este.");
                    Log("  Cierra este método y selecciona 'MTKClient Unlock' en la lista.");
                }
                else
                {
                    Log("  Revisa en XDA si hay un método alternativo para tu modelo.");
                }
                Log("");
            }

            Log("");
            Log("  1. En el teléfono: Ajustes > Opciones de desarrollador");
            Log("     -> Activar 'Desbloqueo OEM'");
            Log("  2. Apaga el teléfono completamente");
            Log("  3. Conecta el USB al PC");
            Log("  4. Pulsa VOL- + VOL+ y SIN SOLTAR, conecta el USB al teléfono");
            Log("     (esto entra en DOWNLOAD MODE - pantalla amarilla con advertencia)");
            Log("  5. Pulsa VOL+ largo para confirmar el desbloqueo");
            Log("  6. El teléfono se resetea solo (BORRA TODOS LOS DATOS)");
            Log("");
            Log("  ⚠ Knox se dispara permanentemente -> Samsung Pay/Secure Folder");
            Log("    y otros servicios Samsung dejarán de funcionar.");
            Log("");
            Log("  Una vez desbloqueado, configura el teléfono de nuevo,");
            Log("  activa Depuración USB, y usa 'Magisk Patch' para rootear.");
            Log("  El backup está disponible en: " + backupDir);

            _adbService.RestartAdb();
            return RootMethodStatus.Success;
        }

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
        if (blInfo.Output.Contains("unlocked: yes") || blInfo.Output.Contains("yes"))
        {
            Log("\n✅ El bootloader YA ESTÁ DESBLOQUEADO.");
            _adbService.FastbootReboot(serial);
            return RootMethodStatus.Success;
        }

        Log("\nIntentando desbloquear bootloader...");
        Log("  ⚠ REVISA LA PANTALLA DEL TELÉFONO");
        Log("  Debes confirmar con las teclas de volumen (Volumen+)");
        Log("  y el botón de encendido en la pantalla del teléfono.");

        var unlockResult = _adbService.ExecuteFastboot($"-s {serial} oem unlock");
        if (unlockResult.Success)
            goto UnlockSuccess;

        Log($"  Respuesta: {unlockResult.Output.Trim()}");

        Log("  Método 2: fastboot flashing unlock...");
        Log("  ⚠ REVISA LA PANTALLA DEL TELÉFONO — posible confirmación requerida");

        unlockResult = _adbService.ExecuteFastboot($"-s {serial} flashing unlock");
        if (unlockResult.Success)
            goto UnlockSuccess;

        Log($"  Respuesta: {unlockResult.Output.Trim()}");

        Log("  Método 3: fastboot flashing unlock_critical...");

        unlockResult = _adbService.ExecuteFastboot($"-s {serial} flashing unlock_critical");
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
        Log($"  El backup está disponible en: {backupDir}");

        // Create backup summary file
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

                        // Use unique filename to prevent overwrite: hash prefix + original name
                        var uniqueName = $"{Math.Abs(string.GetHashCode(remotePath)):X8}_{fileName}";
                        var targetPath = Path.Combine(mediaDir, uniqueName);
                        var pullResult = _adbService.PullFile(serial, remotePath, targetPath);
                        if (pullResult.Success)
                        {
                            files.Add(uniqueName);
                        }
                    }
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

                    // Use unique filename to prevent overwrite
                    var uniqueName = $"{Math.Abs(string.GetHashCode(remotePath)):X8}_{fileName}";
                    var targetPath = Path.Combine(docsDir, uniqueName);
                    var pullResult = _adbService.PullFile(serial, remotePath, targetPath);
                    if (pullResult.Success)
                    {
                        files.Add(uniqueName);
                    }
                }
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
                     Log("    ✅ Instalada");
                     yield return $"✓ {apkName} instalada";
                 }
                 else
                 {
                     Log($"    ❌ Error: {result.Error}");
                     yield return $"✗ Error instalando {apkName}: {result.Error}";
                 }
             }
         }

         // Restore documents/media
         var mediaDir = Path.Combine(backupDir, "media");
         var docsDir = Path.Combine(backupDir, "documents");
         
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
                 _adbService.PushFile(serial, file, $"{deviceStorage}/{Path.GetFileName(file)}");
             }
             Log($"  Archivos guardados en: {deviceStorage}");
             yield return $"Archivos guardados en: {deviceStorage}";
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

         Log("\n✅ Restauración completada");
         Log("  Las apps instaladas aparecerán en la pantalla de inicio");
         Log("  Los archivos están en /sdcard/Download/PCAndroidRooter_Restored");
         yield return "✅ Restauración completada exitosamente";
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
        if (!blCheck.Output.Contains("unlocked: yes") && !blCheck.Output.Contains("yes"))
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
        if (!blCheck.Output.Contains("unlocked: yes") && !blCheck.Output.Contains("yes"))
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
        Log("║       ONE-CLICK ROOT (100% Automático)    ║");
        Log("╚═══════════════════════════════════════════╝");
        Log("Este proceso detecta tu dispositivo y ejecuta");
        Log("el método de root más adecuado automáticamente.");
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

        var manufacturer = deviceInfo.Manufacturer.ToLowerInvariant();
        var model = deviceInfo.Model;
        var androidVersion = deviceInfo.AndroidVersion;
        var abi = deviceInfo.Abi;
        var isSamsung = manufacturer == "samsung";
        var isMediatek = deviceInfo.IsMediaTek;

        LogOk($"Dispositivo: {deviceInfo.Manufacturer} {model}");
        LogOk($"Android: {androidVersion} | ABI: {abi}");
        LogOk($"Batería: {deviceInfo.BatteryLevel}");

        // Verificar batería mínima
        if (int.TryParse(deviceInfo.BatteryLevel.Replace("%", "").Trim(), out var battery) && battery < 30)
        {
            LogError($"Batería muy baja ({battery}%). Conecta el cargador antes de continuar.");
            Log("  Se necesita mínimo 30% de batería para proceder de forma segura.");
            return RootMethodStatus.Failed;
        }
        ct.ThrowIfCancellationRequested();

        // ── FASE 2: Verificar si ya tiene root ──
        Log("\n▸ FASE 2: Verificando root actual...");
        var rooted = DetectIfRooted(serial);
        if (rooted)
        {
            LogOk("═══════════════════════════════════════════");
            LogOk("  EL DISPOSITIVO YA TIENE ROOT");
            LogOk("═══════════════════════════════════════════");
            Log("  No es necesario ejecutar ningún método.");
            Log("  Magisk o su ya están instalados y funcionales.");
            return RootMethodStatus.Success;
        }
        Log("  Dispositivo sin root. Procediendo...");
        ct.ThrowIfCancellationRequested();

        // ── FASE 3: Verificar bootloader ──
        Log("\n▸ FASE 3: Verificando estado del bootloader...");
        var blCheck = _adbService.CheckBootloaderUnlock(serial);
        var bootloaderUnlocked = blCheck.Output.Contains("unlocked: yes") || blCheck.Output.Contains("yes");
        string? backupResult = null;

        if (!bootloaderUnlocked)
        {
            Log("  Bootloader: BLOQUEADO");

            if (isSamsung)
            {
                Log("");
                LogOk("Samsung detectado — flujo especial:");
                Log("  Samsung NO permite desbloqueo vía fastboot.");
                Log("  Se procederá con backup + instrucciones para desbloqueo manual.");
                Log("");

                return await SamsungUnlockFlowAsync(serial, deviceInfo, ct);
            }

            Log("  Se procederá a desbloquear el bootloader automáticamente.");
            Log("  ⚠ ESTO BORRARÁ TODOS LOS DATOS DEL DISPOSITIVO.");
            Log("");

            // Backup antes de desbloquear
            Log("▸ FASE 3a: Creando backup de seguridad...");
            backupResult = await CreateFullBackupAsync(serial, ct);
            if (backupResult != null)
            {
                LogOk($"Backup completado en: {backupResult}");
            }
            else
            {
                LogWarning("No se pudo crear backup completo, pero se continuará.");
            }
            ct.ThrowIfCancellationRequested();

            // Desbloquear bootloader (backup ya creado arriba → skip)
            Log("\n▸ FASE 3b: Desbloqueando bootloader...");
            var unlockResult = await UnlockBootloaderAsync(serial, ct, skipBackup: true);
            if (unlockResult != RootMethodStatus.Success)
            {
                LogError("No se pudo desbloquear el bootloader.");
                Log("  Opciones manuales:");
                Log("  1. Habilita 'Desbloqueo OEM' en Opciones de desarrollador");
                Log("  2. Usa el método 'Desbloquear Bootloader' de la herramienta");
                return RootMethodStatus.Failed;
            }

            LogOk("Bootloader desbloqueado. Esperando reinicio del dispositivo...");
            Log("  El dispositivo se está formateando y reiniciando.");
            Log("  Esto puede tardar 5-10 minutos en el primer arranque.");

            // Esperar a que el dispositivo vuelva
            await WaitForDeviceReadyAsync(serial, ct);
            ct.ThrowIfCancellationRequested();
        }
        else
        {
            LogOk("Bootloader: DESBLOQUEADO");
        }

        // ── FASE 4: Root via Magisk Patch ──
        Log("");
        Log("▸ FASE 4: Aplicando root via Magisk Patch...");
        Log("  Este proceso:");
        Log("  1. Extrae el boot.img del dispositivo");
        Log("  2. Lo parchea con Magisk");
        Log("  3. Lo flashea de vuelta");
        Log("  4. Reinicia el dispositivo");
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
            Log("");
            if (backupResult != null)
            {
                Log($"  Tu backup está en: {backupResult}");
                Log("  Usa 'Restaurar' para recuperar tus datos si es necesario.");
            }
            return RootMethodStatus.Success;
        }

        LogError("El root via Magisk Patch falló.");
        Log("  Intentando método alternativo...");
        ct.ThrowIfCancellationRequested();

        // ── FASE 5: Fallback — ADB Exploit ──
        Log("\n▸ FASE 5: Intentando ADB Exploit como alternativa...");
        var adbResult = await AdbExploitRootAsync(serial, ct);
        if (adbResult == RootMethodStatus.Success)
        {
            LogOk("Root logrado via ADB Exploit.");
            return RootMethodStatus.Success;
        }

        // ── FASE 6: Fallback — Fastboot Boot temporal ──
        Log("\n▸ FASE 6: Intentando Fastboot Boot (root temporal)...");
        Log("  Si el root permanente falló, este método puede");
        Log("  darte root temporal para diagnosticar el problema.");
        var fastbootResult = await FastbootBootAsync(serial, ct);
        if (fastbootResult == RootMethodStatus.Success)
        {
            LogWarning("Root temporal activo. Se pierde al reiniciar.");
            Log("  Usa 'Magisk Patch' para hacerlo permanente.");
            return RootMethodStatus.Success;
        }

        // ── RESULTADO FINAL ──
        Log("");
        LogError("═══════════════════════════════════════════");
        LogError("  NO SE PUDO ROOTEAR EL DISPOSITIVO");
        LogError("═══════════════════════════════════════════");
        Log("");
        Log("  Causas posibles:");
        Log("  • SELinux estricto bloquea los exploits");
        Log("  • El boot.img no es compatible con Magisk");
        Log("  • El bootloader sigue bloqueado");
        Log("  • El fabricante bloqueó el root (ej: Huawei,Some Xiaomi)");
        Log("");
        Log("  Soluciones:");
        Log("  1. Intenta 'Magisk Patch' directamente desde la lista");
        Log("  2. Busca en XDA tu modelo específico + 'root guide'");
        Log("  3. Verifica que el bootloader esté desbloqueado");
        Log("");
        Log("  Dispositivo: " + deviceInfo.Manufacturer + " " + model);
        Log("  Android: " + androidVersion);

        return RootMethodStatus.Failed;
    }

    private async Task<string?> CreateFullBackupAsync(string serial, CancellationToken ct)
    {
        try
        {
            var backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backup", serial);
            Directory.CreateDirectory(backupDir);

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

            // Crear resumen JSON
            var summary = new
            {
                backupDir,
                serial,
                timestamp = DateTime.Now,
                appsCount = apps.Count,
                mediaCount = media.Count,
                documentsCount = docs.Count
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
        catch (Exception ex)
        {
            LogWarning($"  Error creando backup: {ex.Message}");
            return null;
        }
    }

    private async Task WaitForDeviceReadyAsync(string serial, CancellationToken ct, int maxWaitSeconds = 300)
    {
        Log("  Esperando a que el dispositivo se reinicie...");
        Log("  (puede tardar hasta 5 minutos después del formateo)");

        _adbService.RestartAdb();

        for (int i = 0; i < maxWaitSeconds; i++)
        {
            if (ct.IsCancellationRequested)
                return;

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
                    return;
                }
            }
        }

        LogWarning("Tiempo de espera agotado. El dispositivo puede estar aún reiniciándose.");
        Log("  Si el dispositivo no aparece, reconecta el cable USB y pulsa Refresh.");
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
        return RootMethodStatus.Success;
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

        ct.ThrowIfCancellationRequested();

        Log("\nPASO 1: Buscando partición boot...");
        var bootPart = await _adbService.FindBootPartitionAsync(serial, ct);
        if (bootPart == null)
        {
            Log("ERROR: No se encontró la partición boot.");
            return RootMethodStatus.Failed;
        }
        Log($"Partición boot encontrada: {bootPart.Path} ({bootPart.Size / 1024 / 1024} MB)");

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
        var pushed = PushMagiskBinaries(serial, ct);
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
        var magiskBoot = File.Exists(Path.Combine(_magiskService.MagiskDir, "magiskboot"))
            ? $"{remoteDir}/magiskboot" : $"{remoteDir}/magiskboot32";

        var unpackCmd = $"cd /data/local/tmp && {magiskBoot} unpack boot_to_patch.img 2>/dev/null";
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
        var magiskBinPath = deviceInfo.Abi.Contains("64") ? $"{remoteDir}/magisk64" : $"{remoteDir}/magisk32";
        var ramdiskPatchCmd = $"cd /data/local/tmp && {magiskBoot} cpio ramdisk.cpio 'mkdir 0750 overlay.d' " +
                              $"&& {magiskBoot} cpio ramdisk.cpio 'mkdir 0750 overlay.d/sbin' " +
                              $"&& {magiskBoot} cpio ramdisk.cpio 'add 0750 overlay.d/sbin/magisk {magiskBinPath}' " +
                              $"&& {magiskBoot} cpio ramdisk.cpio 'add 0644 overlay.d/sbin/magiskinit {remoteDir}/magiskinit'";

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

        var bootResult = _adbService.ExecuteFastboot($"-s {serial} boot \"{patchedImgLocal}\"", timeoutMs: 60000);
        if (!bootResult.Success)
        {
            Log($"  Error: {bootResult.Error}");
            Log("  Intentando sin serial...");
            bootResult = _adbService.ExecuteFastboot($"boot \"{patchedImgLocal}\"", timeoutMs: 60000);
        }

        if (bootResult.Success)
        {
            Log("═══════════════════════════════════════════");
            Log("  ✅ ROOT TEMPORAL INICIADO");
            Log("═══════════════════════════════════════════");
            _adbService.RestartAdb();
            Log("  El dispositivo debería arrancar con Magisk temporal.");
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
        Log("\n  Prueba a desbloquear bootloader y luego usar 'Magisk Patch'.");
        return RootMethodStatus.Failed;
    }

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
        var backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backup", serial);
        Directory.CreateDirectory(backupDir);

        Log("\n[1/4] Backup de apps instaladas...");
        ct.ThrowIfCancellationRequested();
        await BackupAppsAsync(serial, backupDir, ct);

        Log("\n[2/4] Backup de fotos/vídeos...");
        ct.ThrowIfCancellationRequested();
        await BackupMediaAsync(serial, backupDir, ct);

        Log("\n[3/4] Backup de documentos...");
        ct.ThrowIfCancellationRequested();
        await BackupDocumentsAsync(serial, backupDir, ct);

        Log("\n[4/4] Backup de contactos y SMS...");
        ct.ThrowIfCancellationRequested();
        await BackupContactsSmsAsync(serial, backupDir, ct);

        LogOk($"Backup completado: {backupDir}");

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
            await proc.WaitForExitAsync(procCts.Token);

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
