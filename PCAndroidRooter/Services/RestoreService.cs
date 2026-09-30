using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCAndroidRooter.Models;

namespace PCAndroidRooter.Services;

/// <summary>
/// Restauración de backups (apps APK con verificación de hash, archivos y
/// contactos). Métodos movidos de RootService sin cambios de comportamiento.
/// </summary>
public class RestoreService : RootServiceBase
{
    private readonly AdbService _adbService;

    public RestoreService(AdbService adbService)
    {
        _adbService = adbService;
    }

    public static string CalculateFileSha256(string filePath)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
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
}
