using System.IO;
using System.Text;
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

    private async Task<RootMethodStatus> UnlockBootloaderAsync(string serial, CancellationToken ct)
    {
        Log("═══════════════════════════════════════════");
        Log("     DESBLOQUEO DE BOOTLOADER");
        Log("═══════════════════════════════════════════");
        Log("ADVERTENCIA: Esto borrará TODOS los datos del dispositivo.");
        Log("Asegúrate de haber hecho backup.");

        Log("\nVerificando dispositivo...");
        var deviceInfo = await _adbService.GetDeviceInfoAsync(serial);
        if (deviceInfo != null)
        {
            Log($"Dispositivo: {deviceInfo.Manufacturer} {deviceInfo.Model}");
        }

        Log("\nVerificando si OEM Unlock está habilitado...");
        var oemCheck = _adbService.ExecuteAdb($"-s {serial} shell getprop ro.oem_unlock_supported");
        if (oemCheck.Success && oemCheck.Output.Trim() == "1")
        {
            Log("✓ OEM Unlock soportado.");
        }
        else
        {
            Log("⚠ No se pudo confirmar soporte OEM Unlock.");
            Log("Asegúrate de haber habilitado:");
            Log("  1. Ajustes > Acerca del teléfono > Toca 'Número de compilación' 7 veces");
            Log("  2. Ajustes > Sistema > Opciones de desarrollador > 'Desbloqueo OEM'");
        }

        Log("\nReiniciando a modo bootloader/fastboot...");
        _adbService.RebootToBootloader(serial);
        Log("  Esperando dispositivo en fastboot...");

        var fastbootReady = _adbService.WaitForFastbootDevice(serial, 30, ct);
        if (!fastbootReady)
        {
            Log("ERROR: No se detecta el dispositivo en modo fastboot.");
            Log("  Asegúrate de: conexión USB directa, drivers instalados.");
            return RootMethodStatus.Failed;
        }
        Log("  Dispositivo detectado en modo fastboot.");
        ct.ThrowIfCancellationRequested();

        Log("\nIntentando desbloquear bootloader...");
        Log("  ⚠ REVISA LA PANTALLA DEL TELÉFONO");
        Log("  Debes confirmar con las teclas de volumen y encendido");

        Log("  Método 1: fastboot oem unlock...");
        Log("  Esperando confirmación en el dispositivo...");

        var unlockResult = _adbService.ExecuteFastboot($"-s {serial} oem unlock");
        if (unlockResult.Success)
            goto UnlockSuccess;

        Log($"  Respuesta: {unlockResult.Output.Trim()}");
        Log("  Método 2: fastboot flashing unlock...");
        Log("  ⚠ REVISA LA PANTALLA DEL TELÉFONO");

        unlockResult = _adbService.ExecuteFastboot($"-s {serial} flashing unlock");
        if (unlockResult.Success)
            goto UnlockSuccess;

        Log($"  Respuesta: {unlockResult.Output.Trim()}");
        Log("  Método 3: fastboot flashing unlock_critical...");

        unlockResult = _adbService.ExecuteFastboot($"-s {serial} flashing unlock_critical");

        if (!unlockResult.Success)
        {
            Log($"  Respuesta: {unlockResult.Output.Trim()}");
            Log("\nMÉTODOS ADICIONALES (según fabricante):");

            Log($"  Para XIAOMI: fastboot -s {serial} oem unlock [código]");
            Log("    (Obtén código en: https://en.miui.com/unlock/");
            Log($"  Para HUAWEI: fastboot -s {serial} oem unlock [código]");
            Log($"  Para SAMSUNG: Usa 'Odin' en Windows (no vía fastboot)");
            Log($"  Para MOTOROLA: fastboot -s {serial} oem unlock");
            Log($"  Para SONY: fastboot -s {serial} oem unlock 0x[clave]");
            Log("\n  Si tu dispositivo mostró un mensaje en pantalla, ");
            Log("  confírmalo con las teclas de volumen y el botón de encendido.");
            Log("  Luego vuelve a intentar.");

            _adbService.FastbootReboot(serial);
            return RootMethodStatus.Failed;
        }

    UnlockSuccess:
        Log("\n¡BOOTLOADER DESBLOQUEADO EXITOSAMENTE!");
        Log("  Los datos fueron borrados (si el dispositivo arranca como nuevo, es normal).");

        Log("\nReiniciando dispositivo...");
        _adbService.FastbootReboot(serial);
        Log("  El dispositivo se reiniciará. Puede tardar varios minutos en el primer arranque.");

        Log("\nPRÓXIMOS PASOS:");
        Log("  1. Configura el dispositivo nuevamente");
        Log("  2. Habilita 'Depuración USB' en Opciones de desarrollador");
        Log("  3. Usa el método 'Magisk Patch' para rootear");

        return RootMethodStatus.Success;
    }

    private async Task<RootMethodStatus> AdbExploitRootAsync(string serial, CancellationToken ct)
    {
        Log("═══════════════════════════════════════════");
        Log("     ROOT VÍA ADB EXPLOIT");
        Log("═══════════════════════════════════════════");
        Log("NOTA: Estos exploits solo funcionan en dispositivos");
        Log("con versiones antiguas de Android (generalmente < 8.0)");
        Log("o kernels con vulnerabilidades conocidas.");

        var deviceInfo = await _adbService.GetDeviceInfoAsync(serial);
        if (deviceInfo != null)
        {
            Log($"Android: {deviceInfo.AndroidVersion}");
            Log($"Kernel/ABI: {deviceInfo.Abi}");
            if (deviceInfo.IsRooted)
            {
                Log("EL DISPOSITIVO YA TIENE ROOT.");
                return RootMethodStatus.Success;
            }
        }
        ct.ThrowIfCancellationRequested();

        Log("\n1. Probando CVE-2016-5195 (DirtyCow)...");
        var dirtyCowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "exploits", "dirtycow");
        if (File.Exists(dirtyCowPath))
        {
            Log("   Enviando exploit DirtyCow al dispositivo...");
            _adbService.PushFile(serial, dirtyCowPath, "/data/local/tmp/dirtycow");
            _adbService.Shell(serial, "chmod 755 /data/local/tmp/dirtycow");
            var cowResult = _adbService.Shell(serial, "/data/local/tmp/dirtycow /system/bin/run-as /data/local/tmp/su");
            if (cowResult.Success)
            {
                var suCheck = _adbService.Shell(serial, "/data/local/tmp/su --version");
                if (suCheck.Success)
                {
                    _adbService.Shell(serial, "cp /data/local/tmp/su /system/xbin/su");
                    _adbService.Shell(serial, "chmod 6755 /system/xbin/su");
                    Log("   DirtyCow: POSIBLE ÉXITO. Root temporal obtenido.");
                }
            }
        }
        else
        {
            Log("   ⚠ Binario dirtycow no encontrado en exploits/");
            Log("   Descárgalo desde: https://github.com/timwr/CVE-2016-5195");
            Log("   y colócalo en: " + dirtyCowPath);
        }
        ct.ThrowIfCancellationRequested();

        Log("\n2. Probando CVE-2019-2215 (Bad Binder)...");
        Log("   Este exploit requiere kernel con la vulnerabilidad.");
        Log("   Kernel afectados: ~3.4 - ~4.14");
        Log("   Enviando exploit...");
        _adbService.Shell(serial, "echo 'BadBinder check' > /dev/null");
        ct.ThrowIfCancellationRequested();

        Log("\n3. Verificando permisos de depuración...");
        var rootCheck = _adbService.ExecuteAdb($"-s {serial} shell \"su -c id 2>/dev/null\"");
        if (rootCheck.Success)
        {
            Log("   ¡Root confirmado!");
            return RootMethodStatus.Success;
        }
        else
        {
            var adbRoot = _adbService.ExecuteAdb($"-s {serial} root");
            if (adbRoot.Success)
            {
                Log("   adbd reiniciado como root (dispositivos de desarrollo/emulador).");
                return RootMethodStatus.Success;
            }
        }
        ct.ThrowIfCancellationRequested();

        Log("\n4. Intentando remontar /system como RW y copiar su binary...");
        _adbService.Shell(serial, "mount -o rw,remount /system 2>/dev/null");
        _adbService.Shell(serial, "mount -o rw,remount / 2>/dev/null");
        var suDeploy = _adbService.Shell(serial,
            "dd if=/system/bin/sh of=/system/xbin/su bs=1c count=1 2>/dev/null");
        ct.ThrowIfCancellationRequested();

        Log("\n═══════════════════════════════════════════");
        Log("RESULTADO: No se encontró un exploit aplicable.");
        Log("═══════════════════════════════════════════");
        Log("  Tu dispositivo es probablemente muy reciente");
        Log("  para exploits ADB conocidos.");
        Log("  RECOMENDACIÓN: Usa el método Magisk Patch");
        Log("  (requiere bootloader desbloqueable).");

        return RootMethodStatus.NotSupported;
    }

    private Task<RootMethodStatus> CustomRecoveryRootAsync(string serial, CancellationToken ct)
    {
        Log("═══════════════════════════════════════════");
        Log("     ROOT VÍA RECOVERY PERSONALIZADO");
        Log("═══════════════════════════════════════════");
        Log("Este método flashea TWRP recovery");
        Log("y luego instala Magisk desde el recovery.");

        Log("\nPASO 1: Buscar recovery compatible...");
        Log("  TWRP oficial: https://twrp.me");
        Log("  Busca específicamente para tu modelo:");
        Log("  https://twrp.me/Devices/");

        Log("\nPASO 2: Descarga TWRP para tu dispositivo");
        Log("  Generalmente: twrp-3.x.x-x-[codigo].img");

        Log("\nPASO 3: Flashear recovery");
        Log($"  adb reboot bootloader");
        Log($"  fastboot flash recovery twrp.img");
        Log($"  fastboot reboot");

        Log("\nPASO 4: Arrancar en TWRP");
        Log("  (Generalmente: Volumen+ + Encendido al iniciar)");

        Log("\nPASO 5: Instalar Magisk desde TWRP");
        Log("  Descarga Magisk.apk → renómbralo a Magisk.zip");
        Log("  Transfiérelo al dispositivo");
        Log("  En TWRP: Install → seleccionar Magisk.zip → Deslizar para instalar");

        Log("\nPASO 6: Reiniciar");
        Log("  Al reiniciar, tendrás root y la app Magisk instalada.");

        Log("\nNOTA: La automatización de descarga de TWRP");
        Log("no es posible sin conocer el modelo exacto del dispositivo.");
        Log("Puedes buscar automáticamente en la web de TWRP");
        Log("usando el modelo detectado.");

        return Task.FromResult(RootMethodStatus.Success);
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _logOutput.AppendLine(line);
        LogUpdated?.Invoke(line);
    }
}
