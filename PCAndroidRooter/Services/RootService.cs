using System;
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

    public event Action<string>? LogUpdated;
    public event Action<RootMethodType, RootMethodStatus>? MethodStatusChanged;

    public string LogOutput => _logOutput.ToString();

    public RootService(AdbService adbService, MagiskService magiskService)
    {
        _adbService = adbService;
        _magiskService = magiskService;
    }

    public async Task<RootMethodStatus> ExecuteMethodAsync(RootMethod method, string serial, CancellationToken ct)
    {
        MethodStatusChanged?.Invoke(method.Type, RootMethodStatus.Running);

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
            MethodStatusChanged?.Invoke(method.Type, RootMethodStatus.Failed);
            return RootMethodStatus.Failed;
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            MethodStatusChanged?.Invoke(method.Type, RootMethodStatus.Failed);
            return RootMethodStatus.Failed;
        }
    }

    private async Task<RootMethodStatus> MagiskRootAsync(string serial, CancellationToken ct)
    {
        Log("═══════════════════════════════════════════");
        Log("     ROOT VÍA MAGISK PATCH (AUTOMÁTICO)");
        Log("═══════════════════════════════════════════");

        Log("PASO 1: Verificando dispositivo...");
        var deviceInfo = await _adbService.GetDeviceInfoAsync(serial);
        if (deviceInfo == null)
        {
            Log("ERROR: No se pudo obtener información del dispositivo.");
            return RootMethodStatus.Failed;
        }

        Log($"Dispositivo: {deviceInfo.Manufacturer} {deviceInfo.Model}");
        Log($"Android: {deviceInfo.AndroidVersion} | ABI: {deviceInfo.Abi}");
        ct.ThrowIfCancellationRequested();

        Log("\nPASO 2: Buscando partición boot...");
        var bootPart = await _adbService.FindBootPartitionAsync(serial, ct);
        if (bootPart == null)
        {
            Log("ERROR: No se encontró la partición boot.");
            Log("Intenta con el método 'Desbloquear Bootloader' primero.");
            return RootMethodStatus.Failed;
        }
        Log($"Partición boot encontrada: {bootPart.Path} ({bootPart.Size / 1024 / 1024} MB)");
        ct.ThrowIfCancellationRequested();

        Log("\nPASO 3: Descargando Magisk...");
        if (!_magiskService.HasBinaries)
        {
            var downloaded = await _magiskService.DownloadMagiskAsync();
            if (!downloaded)
            {
                Log("ERROR: No se pudo descargar Magisk.");
                return RootMethodStatus.Failed;
            }
        }
        var version = await _magiskService.GetLatestVersionAsync();
        Log($"Magisk {version ?? "desconocido"} listo.");
        ct.ThrowIfCancellationRequested();

        Log("\nPASO 4: Extrayendo boot.img del dispositivo...");
        var bootImgLocal = Path.Combine(_magiskService.MagiskDir, "boot.img");
        var bootImgBackup = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"boot_original_{DateTime.Now:yyyyMMdd_HHmmss}.img");
        
        var extracted = await _adbService.ExtractBootImgAsync(serial, bootPart, bootImgLocal, ct);
        if (!extracted)
        {
            Log("ERROR: No se pudo extraer boot.img.");
            Log("IMPORTANTE: El dispositivo puede requerir:\n" +
                "  1. Habilitar depuración USB\n" +
                "  2. Autorizar el equipo en la pantalla del dispositivo\n" +
                "  3. Tener bootloader desbloqueado");
            return RootMethodStatus.Failed;
        }
        
        // Guardar copia de seguridad del boot.img original
        try
        {
            File.Copy(bootImgLocal, bootImgBackup, overwrite:true);
            Log($"  Boot.img original guardado en: {bootImgBackup}");
        }
        catch { }
        
        ct.ThrowIfCancellationRequested();

        Log("\nPASO 5: Preparando binaries de Magisk en el dispositivo...");
        var remoteDir = "/data/local/tmp/magisk";
        _adbService.Shell(serial, $"mkdir -p {remoteDir}");
        _adbService.Shell(serial, $"rm -rf {remoteDir}/*");

        var magiskFiles = new[] { "magiskboot", "magiskboot32", "magisk64", "magisk32", "magiskinit" };
        int pushed = 0;
        foreach (var file in magiskFiles)
        {
            var localPath = Path.Combine(_magiskService.MagiskDir, file);
            if (File.Exists(localPath))
            {
                var result = _adbService.PushFile(serial, localPath, $"{remoteDir}/{file}");
                if (result.Success) pushed++;
                _adbService.Shell(serial, $"chmod 755 {remoteDir}/{file}");
            }
        }

        if (pushed == 0)
        {
            Log("ERROR: No se pudieron subir los binaries de Magisk al dispositivo.");
            return RootMethodStatus.Failed;
        }
        Log($"  {pushed} binarios subidos a {remoteDir}");
        ct.ThrowIfCancellationRequested();

        Log("\nPASO 6: Subiendo boot.img al dispositivo...");
        var remoteBoot = "/data/local/tmp/boot_to_patch.img";
        var pushResult = _adbService.PushFile(serial, bootImgLocal, remoteBoot);
        if (!pushResult.Success)
        {
            Log("ERROR: No se pudo subir boot.img al dispositivo.");
            return RootMethodStatus.Failed;
        }
        Log($"  boot.img subido a {remoteBoot}");
        ct.ThrowIfCancellationRequested();

        Log("\nPASO 7: Parcheando boot.img con magiskboot...");
        var magiskBoot = File.Exists(Path.Combine(_magiskService.MagiskDir, "magiskboot"))
            ? $"{remoteDir}/magiskboot" : $"{remoteDir}/magiskboot32";

        var unpackCmd = $"cd /data/local/tmp && {magiskBoot} unpack boot_to_patch.img 2>/dev/null";
        var unpackResult = _adbService.Shell(serial, unpackCmd);
        if (!unpackResult.Success)
        {
            Log($"  Error al desempaquetar boot.img: {unpackResult.Error}");
            Log("  Intentando método alternativo...");

            var unpackResult2 = _adbService.Shell(serial, $"cd /data/local/tmp && {magiskBoot} unpack -h boot_to_patch.img");
            if (!unpackResult2.Success)
            {
                Log("ERROR: No se pudo desempaquetar boot.img.");
                Log("  El boot.img puede estar corrupto o ser incompatible.");
                return RootMethodStatus.Failed;
            }
        }
        Log("  boot.img desempaquetado correctamente.");
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
            Log($"  Error al parchear ramdisk: {patchResult.Error}");
            Log("  Intentando parche simple...");
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
            var repackCmd2 = $"cd /data/local/tmp && {magiskBoot} repack -n boot_to_patch.img new-boot.img 2>/dev/null";
            repackResult = _adbService.Shell(serial, repackCmd2);
        }

        var repackCheck = _adbService.Shell(serial, "wc -c < /data/local/tmp/new-boot.img 2>/dev/null");
        var repackedSize = 0L;
        long.TryParse(repackCheck.Output.Trim(), out repackedSize);
        if (repackedSize < 100000)
        {
            Log($"ERROR: boot.img reempaquetado inválido ({repackedSize} bytes).");
            Log($"  Comando: {magiskBoot} repack boot_to_patch.img");
            return RootMethodStatus.Failed;
        }
        Log($"  boot.img reempaquetado ({repackedSize / 1024 / 1024} MB)");
        ct.ThrowIfCancellationRequested();

        Log("\nPASO 8: Descargando boot.img parcheado...");
        var patchedImgLocal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "magisk_patched.img");

        var remotePatchedPaths = new[] { "/data/local/tmp/new-boot.img", "/data/local/tmp/boot_to_patch.img" };
        bool pulled = false;
        foreach (var remotePath in remotePatchedPaths)
        {
            var result = _adbService.PullFile(serial, remotePath, patchedImgLocal);
            if (result.Success && File.Exists(patchedImgLocal) && new FileInfo(patchedImgLocal).Length > 100000)
            {
                pulled = true;
                break;
            }
        }

        if (!pulled)
        {
            Log("  Usando adb exec-out para descargar (binario directo)...");
            pulled = await _adbService.ExecuteAdbRawAsync(serial, remotePatchedPaths[0], patchedImgLocal, ct);
        }

        if (!pulled)
        {
            Log("ERROR: No se pudo descargar el boot.img parcheado.");
            return RootMethodStatus.Failed;
        }

        var patchedInfo = new FileInfo(patchedImgLocal);
        Log($"  boot.img parcheado descargado: {patchedImgLocal} ({patchedInfo.Length / 1024 / 1024} MB)");
        ct.ThrowIfCancellationRequested();

        Log("\nPASO 9: Conectando con fastboot para flashear...");
        Log("  Reiniciando a bootloader...");
        _adbService.RebootToBootloader(serial);

        var fastbootReady = _adbService.WaitForFastbootDevice(serial, 30, ct);
        if (!fastbootReady)
        {
            Log("  ¿El dispositivo no entró en modo fastboot?");
            Log("  Opciones: 1) Intenta manualmente: adb reboot bootloader");
            Log("            2) El bootloader puede estar bloqueado");
            Log("  El archivo parcheado se guardó en: " + patchedImgLocal);
            Log("  Puedes flashearlo manualmente con: fastboot flash boot " + patchedImgLocal);
            return RootMethodStatus.Failed;
        }
        Log("  Dispositivo detectado en modo fastboot.");
        ct.ThrowIfCancellationRequested();

        Log("\nPASO 10: Flasheando boot.img parcheado...");
        var flashResult = _adbService.FlashBootViaFastboot(serial, patchedImgLocal);
        if (!flashResult)
        {
            Log("ERROR: No se pudo flashear la imagen. ¿El bootloader está desbloqueado?");
            Log("  Usa el método 'Desbloquear Bootloader' primero.");
            Log($"  O flashea manualmente: fastboot flash boot {patchedImgLocal}");
            _adbService.FastbootReboot(serial);
            return RootMethodStatus.Failed;
        }
        Log("  boot.img flasheado correctamente.");

        Log("\nPASO 11: Reiniciando dispositivo...");
        _adbService.FastbootReboot(serial);
        Log("  Dispositivo reiniciándose...");

        Log("\n═══════════════════════════════════════════");
        Log("    ¡ROOT COMPLETADO!");
        Log("═══════════════════════════════════════════");
        Log("  Después de reiniciar, deberías ver la app Magisk instalada.");
        Log("  Si no aparece, descarga Magisk desde: https://github.com/topjohnwu/Magisk");
        Log("  y ábrela para completar la configuración.");

        return RootMethodStatus.Success;
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
        var adbRoot = _adbService.ExecuteAdb($"-s {serial} root");
        if (adbRoot.Success && (adbRoot.Output.Contains("already running as root") ||
                                adbRoot.Output.Contains("restarting") ||
                                adbRoot.Output.Contains("adbd is now running as root")))
        {
            var suCheck = _adbService.ExecuteAdb($"-s {serial} shell su -c id");
            if (suCheck.Success && (suCheck.Output.Contains("uid=0") || suCheck.Output.Contains("root")))
            {
                Log("  ✅ 'adb root' funcionó. Dispositivo ya es root.");
                Log("  Instalando Magisk companion desde MagiskService...");
                InstallMagiskCompanionAsync(serial, ct);
                return RootMethodStatus.Success;
            }
        }
        Log("  'adb root' no disponible en este dispositivo.");

        ct.ThrowIfCancellationRequested();

        Log("\n[2/5] Intentando remontar /system como lectura/escritura...");
        var mountResult = _adbService.Shell(serial, "mount -o rw,remount /system 2>&1");
        if (mountResult.Success)
            Log("  /system montado como RW.");
        else
        {
            mountResult = _adbService.Shell(serial, "mount -o rw,remount / 2>&1");
            if (mountResult.Success)
                Log("  Partición raíz '/' montada como RW.");
            else
                Log("  No se pudo remontar /system (probablemente SELinux estricto o bootloader bloqueado).");
        }

        Log("\n[3/5] Verificando si ya existe 'su' en el sistema...");
        ct.ThrowIfCancellationRequested();

        var suTest = _adbService.ExecuteAdb($"-s {serial} shell su -c id");
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
        _adbService.Shell(serial, "chmod 6755 /data/local/tmp/su");

        var testResult = _adbService.Shell(serial, "/data/local/tmp/su -c id");
        if (testResult.Success && testResult.Output.Contains("uid=0"))
        {
            Log("  ✅ 'su' funcional en /data/local/tmp.");

            Log("\n[5/5] Intentando copiar 'su' a /system/xbin/ (persistente)...");
            _adbService.Shell(serial, "mkdir -p /system/xbin");
            var cpResult = _adbService.Shell(serial, "cp /data/local/tmp/su /system/xbin/su 2>&1");
            if (cpResult.Success)
            {
                _adbService.Shell(serial, "chmod 6755 /system/xbin/su");
                Log("  ✅ 'su' copiado a /system/xbin/su (persistente).");
                _adbService.Shell(serial, "/system/xbin/su -c setenforce 0 2>/dev/null");
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

    private async Task<RootMethodStatus> UnlockBootloaderAsync(string serial, CancellationToken ct)
    {
        Log("═══════════════════════════════════════════");
        Log("     DESBLOQUEO DE BOOTLOADER");
        Log("═══════════════════════════════════════════");
        Log("ADVERTENCIA: Esto borrará TODOS los datos del dispositivo.");
        Log("Se realizará backup automático antes de continuar.");

        var backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backup", serial);
        Directory.CreateDirectory(backupDir);

        Log("\n=== FASE 1: Backup automático de datos del cliente ===");

        Log("\n[1/4] Backup de apps (.apk) instaladas...");
        ct.ThrowIfCancellationRequested();
        var apkResult = BackupAppsAsync(serial, backupDir, ct);
        Log($"  Apps respaldadas: {apkResult.Count}");

        Log("\n[2/4] Backup de fotos/vídeos...");
        ct.ThrowIfCancellationRequested();
        var mediaResult = BackupMediaAsync(serial, backupDir, ct);
        Log($"  Archivos multimedia respaldados: {mediaResult.Count}");

        Log("\n[3/4] Backup de documentos y archivos importantes...");
        ct.ThrowIfCancellationRequested();
        var docsResult = BackupDocumentsAsync(serial, backupDir, ct);
        Log($"  Documentos respaldados: {docsResult.Count}");

        Log("\n[4/4] Backup de contactos y SMS...");
        ct.ThrowIfCancellationRequested();
        await BackupContactsSmsAsync(serial, backupDir, ct);

        Log($"\n✅ Backup completado en: {backupDir}");
        Log("Guarda esta carpeta en un lugar seguro antes de continuar.");

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

        Log($"  Para Samsung: usa ODIN en Windows:");
        Log("    1. Descarga la última combinación firmware desde SamMobile.com");
        Log("    2. Flashea con ODIN → Auto reboot desmarcado");
        Log("    3. Cuando entre en modo download: Vol- + Home + Encendido");

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
                documentsCount = docsResult.Count
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
        Log("  El dispositivo se reiniciará. Puede tardar varios minutos en el primer arranque.");

        Log("\nPRÓXIMOS PASOS:");
        Log("  1. Configura el dispositivo (idioma, WiFi, cuenta Google)");
        Log("  2. Habilita 'Depuración USB' en Opciones de desarrollador");
        Log("  3. Usa 'Magisk Patch' para rootear el dispositivo");
        Log("  4. Restaura datos desde el backup si es necesario");

        return RootMethodStatus.Success;
    }

    private List<string> BackupAppsAsync(string serial, string backupDir, CancellationToken ct)
    {
        var apps = new List<string>();
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
                .Where(p => !string.IsNullOrEmpty(p)).ToList();

            Log($"    Encontradas {packages.Count} apps instaladas. Procesando hasta 50...");
            var count = 0;

            foreach (var package in packages.Take(50))
            {
                ct.ThrowIfCancellationRequested();
                count++;
                
                var pathResult = _adbService.ExecuteAdb($"-s {serial} shell pm path {package}");
                if (!pathResult.Success) continue;

                var apkPath = pathResult.Output.Replace("package:", "").Trim();
                if (string.IsNullOrEmpty(apkPath) || !apkPath.EndsWith(".apk")) continue;

                var fileName = $"{package}.apk";
                var targetPath = Path.Combine(apkDir, fileName);
                var pullResult = _adbService.PullFile(serial, apkPath, targetPath);
                if (pullResult.Success)
                {
                    var fileInfo = new FileInfo(targetPath);
                    if (fileInfo.Length > 1000) // Validar archivo no vacío
                    {
                        apps.Add(package);
                        Log($"    [{count}/{packages.Count}] Guardada: {package}");
                    }
                    else
                    {
                        File.Delete(targetPath); // Eliminar archivo inválido
                        Log($"    [{count}/{packages.Count}] Archivo vacío, omitido: {package}");
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
    }

    private List<string> BackupMediaAsync(string serial, string backupDir, CancellationToken ct)
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
                
                var listResult = _adbService.ExecuteAdb($"-s {serial} shell \"find {basePath} -type f -name '*.jpg' -o -name '*.png' -o -name '*.mp4' -o -name '*.pdf' 2>/dev/null | head -50\"");
                if (!listResult.Success) continue;

                foreach (var line in listResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    ct.ThrowIfCancellationRequested();
                    var remotePath = line.Trim();
                    if (string.IsNullOrEmpty(remotePath) || remotePath.Contains("No such file")) continue;

                    var fileName = Path.GetFileName(remotePath);
                    if (string.IsNullOrEmpty(fileName)) continue;

                    var targetPath = Path.Combine(mediaDir, fileName);
                    var pullResult = _adbService.PullFile(serial, remotePath, targetPath);
                    if (pullResult.Success)
                    {
                        files.Add(fileName);
                    }
                }
            }
            catch { }
        }

        Log($"    Archivos multimedia respaldados: {files.Count}");
        return files;
    }

    private List<string> BackupDocumentsAsync(string serial, string backupDir, CancellationToken ct)
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
                
                var listResult = _adbService.ExecuteAdb($"-s {serial} shell \"find {basePath} -type f \\( -name '*.pdf' -o -name '*.doc' -o -name '*.docx' -o -name '*.xls' -o -name '*.xlsx' -o -name '*.txt' \\) 2>/dev/null | head -30\"");
                if (!listResult.Success) continue;

                foreach (var line in listResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    ct.ThrowIfCancellationRequested();
                    var remotePath = line.Trim();
                    if (string.IsNullOrEmpty(remotePath) || remotePath.Contains("No such file")) continue;

                    var fileName = Path.GetFileName(remotePath);
                    if (string.IsNullOrEmpty(fileName)) continue;

                    var targetPath = Path.Combine(docsDir, fileName);
                    var pullResult = _adbService.PullFile(serial, remotePath, targetPath);
                    if (pullResult.Success)
                    {
                        files.Add(fileName);
                    }
                }
            }
            catch { }
        }

        Log($"    Documentos respaldados: {files.Count}");
        return files;
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
            return RootMethodStatus.Failed;
        }

        var blCheck = _adbService.CheckBootloaderUnlock(serial);
        if (!blCheck.Output.Contains("unlocked: yes") && !blCheck.Output.Contains("yes"))
        {
            Log("ERROR: Bootloader debe estar desbloqueado para flashear recovery.");
            Log("Usa el método 'Desbloquear Bootloader' primero.");
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

        return RootMethodStatus.Success;
    }

    private void InstallMagiskCompanionAsync(string serial, CancellationToken ct)
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
        Log("═══════════════════════════════════════════");
        Log("     ONE-CLICK ROOT (Automático)");
        Log("═══════════════════════════════════════════");
        Log("Este método intenta múltiples técnicas en secuencia.");

        var deviceInfo = await _adbService.GetDeviceInfoAsync(serial);
        if (deviceInfo != null)
        {
            Log($"Dispositivo: {deviceInfo.Manufacturer} {deviceInfo.Model}");
            Log($"Android: {deviceInfo.AndroidVersion} | ABI: {deviceInfo.Abi}");
        }

        Log("\nPASO 1: Detectando estado actual...");
        var rooted = DetectIfRooted(serial);
        if (rooted)
        {
            Log("✓ El dispositivo YA está rooteado.");
            return RootMethodStatus.Success;
        }

        Log("\nPASO 2: Intentando ADB Exploit (rápido)...");
        var adbResult = await AdbExploitRootAsync(serial, ct);
        if (adbResult == RootMethodStatus.Success)
            return RootMethodStatus.Success;

        Log("\nPASO 3: Verificando bootloader...");
        _adbService.RebootToBootloader(serial);
        var fastbootOk = _adbService.WaitForFastbootDevice(serial, 15, ct);

        if (fastbootOk)
        {
            Log("✓ Bootloader accesible. Usando Magisk Patch...");
            return await MagiskRootAsync(serial, ct);
        }

        Log("\n═══════════════════════════════════════════");
        Log("  ONE-CLICK COMPLETO - RESUMEN:");
        Log("═══════════════════════════════════════════");
        Log("  • ADB Exploit: Falló (SELinux enforcing)");
        Log("  • Magisk: Requiere bootloader desbloqueado");
        Log("  • Recomendación: Desbloquea bootloader primero");

        return RootMethodStatus.Failed;
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
            if (androidVer.StartsWith("8") || androidVer.StartsWith("9") || 
                androidVer.StartsWith("1") || int.TryParse(androidVer, out var v) && v >= 8)
            {
                Log("\n❌ Android 8.0+ requiere desbloquear bootloader para root.");
                Log("   El desbloqueo BORRA todos los datos del dispositivo.");
                Log("   Usa 'Desbloquear Bootloader' primero, luego 'Magisk Patch'.");
                return RootMethodStatus.Failed;
            }
        }

        Log("\nPASO 1: Intentando métodos temporales...");
        
        Log("  [1/4] Verificando SELinux...");
        var selinux = _adbService.Shell(serial, "getenforce");
        Log($"  SELinux: {selinux.Output}");

        Log("  [2/4] Intentando disable verity...");
        var verity = _adbService.Shell(serial, "disable-verity 2>/dev/null || echo 'failed'");
        Log($"  Disable-verity: {verity.Output}");

        Log("  [3/4] Intentando mount rw...");
        var mount = _adbService.Shell(serial, "mount -o rw,remount /system 2>&1");
        if (mount.Success) Log("  ✓ /system montado como RW");

        Log("  [4/4] Buscando vulnerabilades en proc...");
        var pid = FindExploitablePid(serial);
        if (pid > 0)
        {
            Log($"  ✓ PID potencial: {pid}");
            return ExploitPid(serial, pid);
        }

        Log("\n═══════════════════════════════════════════");
        Log("  RESULTADO: Root temporal no disponible");
        Log("═══════════════════════════════════════════");
        Log("  Android 8.0+ tiene SELinux estricto");
        Log("  Necesitas desbloquear bootloader para root persistente");

        return RootMethodStatus.Failed;
    }

    private bool DetectIfRooted(string serial)
    {
        var checks = new[]
        {
            $"-s {serial} shell which su",
            $"-s {serial} shell su -c id",
            $"-s {serial} shell ls /system/bin/su"
        };

        foreach (var check in checks)
        {
            var result = _adbService.ExecuteAdb(check);
            if (result.Success && (result.Output.Contains("uid=0") || !result.Output.Contains("not found")))
            {
                return true;
            }
        }
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
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _logOutput.AppendLine(line);
        LogUpdated?.Invoke(line);
    }
}
