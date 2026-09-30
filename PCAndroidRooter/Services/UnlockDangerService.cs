using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCAndroidRooter.Models;

namespace PCAndroidRooter.Services;

/// <summary>
/// Zona de peligro: desbloqueo de bootloader (OEM/fastboot), Samsung Download
/// Mode y MTKClient. Todo lo que hay aquí FORMATEA el teléfono. No es root:
/// el flujo de root solo puede llegar a estas operaciones a través de
/// <see cref="IUnlockDanger"/> (gate auditable) — nunca por llamadas directas.
/// Métodos movidos de RootService sin cambios de comportamiento.
/// </summary>
/// <remarks>
/// Los métodos públicos son "internal-friendly": solo la fachada
/// <see cref="RootService"/> y <see cref="IUnlockDanger"/> los consumen.
/// </remarks>
public class UnlockDangerService : RootServiceBase, IUnlockDanger
{
    private readonly AdbService _adbService;
    private readonly MagiskService _magiskService;

    public UnlockDangerService(AdbService adbService, MagiskService magiskService)
    {
        _adbService = adbService;
        _magiskService = magiskService;
    }

    // ════════════════════════════════════════════════════════════
    // Gates compartidos con el root (llamados vía IUnlockDanger)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Gate de batería fail-closed: exige lectura fiable ≥ 30% antes de
    /// procesos largos (wipe/flash) para no apagarse a mitad del proceso.
    /// Antes un "Desconocido" saltaba el check (fail-open).
    /// </summary>
    public async Task<bool> EnsureMinBatteryAsync(string serial, DeviceInfo? deviceInfo = null)
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

    /// <summary>
    /// Gate del root hacia el unlock: si el bootloader está cerrado CONFIRMADO,
    /// lo desbloquea (formatea). Devuelve null = seguir con el parche.
    /// Un estado = parar.
    /// </summary>
    async Task<RootMethodStatus?> IUnlockDanger.EnsureBootloaderForRootAsync(string serial, CancellationToken ct)
        => await EnsureBootloaderForRootAsync(serial, ct);

    async Task<bool> IUnlockDanger.EnsureMinBatteryForRootAsync(string serial, DeviceInfo? deviceInfo, CancellationToken ct)
        => await EnsureMinBatteryAsync(serial, deviceInfo);

    /// <summary>null = seguir con el parche. Un estado = parar.</summary>
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

    /// <summary>
    /// Carpeta nueva por intento. Reutilizar backup/{serial} hacía que archivos
    /// de un intento viejo contaran como backup de hoy y autorizaran el wipe.
    /// </summary>
    public static string CreateFreshBackupDirectory(string serial)
    {
        var dir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "backup",
            AdbService.SanitizeSerialForPath(serial),
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ════════════════════════════════════════════════════════════
    // Desbloqueo de bootloader (formatea)
    // ════════════════════════════════════════════════════════════

    public async Task<RootMethodStatus> UnlockBootloaderAsync(string serial, CancellationToken ct)
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

    // ════════════════════════════════════════════════════════════
    // Backups (parciales; autorizan wipe solo con archivos de ESTE intento)
    // ════════════════════════════════════════════════════════════

    public async Task<Dictionary<string, string>> BackupAppsAsync(string serial, string backupDir, CancellationToken ct)
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
                            var hash = RestoreService.CalculateFileSha256(targetPath);
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

    public async Task<List<string>> BackupMediaAsync(string serial, string backupDir, CancellationToken ct)
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

    public async Task<List<string>> BackupDocumentsAsync(string serial, string backupDir, CancellationToken ct)
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

    public async Task BackupContactsSmsAsync(string serial, string backupDir, CancellationToken ct)
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

    private static string StableNameHash(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes.AsSpan(0, 4));
    }

    // ════════════════════════════════════════════════════════════
    // MTKClient (formatea)
    // ════════════════════════════════════════════════════════════

    public async Task<RootMethodStatus> MtkClientUnlockAsync(string serial, CancellationToken ct)
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

/// <summary>
/// Superficie del unlock que el flujo de root puede usar. Mínima a propósito:
/// solo gates de batería y bootloader. El root NO puede invocar unlock/MTK
/// por ninguna otra vía.
/// </summary>
public interface IUnlockDanger
{
    Task<RootMethodStatus?> EnsureBootloaderForRootAsync(string serial, CancellationToken ct);
    Task<bool> EnsureMinBatteryForRootAsync(string serial, DeviceInfo? deviceInfo, CancellationToken ct);
}
