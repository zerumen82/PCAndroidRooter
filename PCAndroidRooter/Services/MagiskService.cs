using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PCAndroidRooter.Services;

public class MagiskService
{
    private readonly HttpClient _httpClient;
    private string _workDir;
    private const string RepoApi = "https://api.github.com/repos/topjohnwu/Magisk/releases/latest";

    public event Action<string>? Log;
    public event Action<double>? Progress;

    public string MagiskDir => Path.Combine(_workDir, "magisk");
    public string? ExtractedBootImg { get; private set; }
    public string? PatchedBootImg { get; private set; }

    // Mapeo de ABI de Android → nombre de archivo magiskboot extraído desde el APK
    // En Magisk v24+, los binaries están en lib/$ABI/lib*.so del APK
    private static readonly Dictionary<string, string> AbiBootMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // 64-bit ARM → magiskboot
        ["arm64-v8a"] = "magiskboot",
        ["aarch64"]   = "magiskboot",
        // 32-bit ARM → magiskboot32
        ["armeabi-v7a"] = "magiskboot32",
        ["armv7"]       = "magiskboot32",
        // x86_64 → magiskboot
        ["x86_64"]      = "magiskboot",
        // x86   → magiskboot32
        ["x86"]         = "magiskboot32",
    };

    private static readonly string[] SupportedArchs =
        { "arm64-v8a", "armeabi-v7a", "x86_64", "x86" };

    public MagiskService()
    {
        _workDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "magisk_work");
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PCAndroidRooter", "1.0"));
        _httpClient.Timeout = TimeSpan.FromMinutes(10);
    }

    public async Task<string?> GetLatestVersionAsync()
    {
        try
        {
            var response = await _httpClient.GetStringAsync(RepoApi);
            var release = JsonSerializer.Deserialize<GitHubRelease>(response);
            return release?.TagName;
        }
        catch
        {
            Log?.Invoke("No se pudo obtener la última versión de Magisk.");
            return null;
        }
    }

    public async Task<bool> DownloadMagiskAsync()
    {
        Directory.CreateDirectory(MagiskDir);

        try
        {
            Log?.Invoke("Obteniendo última versión de Magisk desde GitHub...");
            var response = await _httpClient.GetStringAsync(RepoApi);
            var release = JsonSerializer.Deserialize<GitHubRelease>(response);
            if (release?.Assets == null || release.Assets.Count == 0)
            {
                Log?.Invoke("No se encontraron assets en el release de Magisk.");
                return false;
            }

            // Buscar el APK de Magisk (Magisk-vXX.X.apk — contiene los binaries de arranque)
            var apkAsset = release.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("Magisk", StringComparison.OrdinalIgnoreCase));

            if (apkAsset == null)
            {
                Log?.Invoke("No se encontró el APK de Magisk en el release.");
                return false;
            }

            Log?.Invoke($"Descargando {apkAsset.Name} ({apkAsset.Size / 1024 / 1024} MB)...");

            var apkPath = Path.Combine(MagiskDir, "magisk.apk");
            using (var dlStream = await _httpClient.GetStreamAsync(apkAsset.BrowserDownloadUrl))
            using (var fileStream = File.Create(apkPath))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await dlStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    totalRead += read;
                    if (apkAsset.Size > 0)
                        Progress?.Invoke((double)totalRead / apkAsset.Size);
                }
            }

            // Verificar SHA256 del APK descargado (hash en release body de GitHub)
            string? expectedHash = null;
            if (!string.IsNullOrEmpty(release.Body))
            {
                // Buscar SHA256 en el body del release (formato: "sha256: <hash>" o "<hash>  <filename>")
                var lines = release.Body.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains(apkAsset.Name, StringComparison.OrdinalIgnoreCase) &&
                        Regex.IsMatch(line, @"[a-fA-F0-9]{64}"))
                    {
                        var match = Regex.Match(line, @"([a-fA-F0-9]{64})");
                        if (match.Success)
                        {
                            expectedHash = match.Groups[1].Value;
                            break;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(expectedHash))
            {
                Log?.Invoke("Verificando integridad del APK (SHA256)...");
                var downloadedHash = CalculateSha256(apkPath);
                if (!downloadedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    Log?.Invoke($"ERROR: SHA256 no coincide!");
                    Log?.Invoke($"  Esperado: {expectedHash}");
                    Log?.Invoke($"  Obtenido: {downloadedHash}");
                    Log?.Invoke("  El APK puede estar corrupto o haber sido manipulado.");
                    Log?.Invoke("  Descarga abortada por seguridad.");
                    try { File.Delete(apkPath); } catch { }
                    return false;
                }
                Log?.Invoke($"  SHA256 verificado: {downloadedHash[..16]}...");
            }
            else
            {
                Log?.Invoke("ADVERTENCIA: No hay hash SHA256 disponible en el release.");
                Log?.Invoke("  No se puede verificar la integridad del APK descargado.");
            }

            Log?.Invoke("APK descargado. Extrayendo binaries de arranque del APK...");
            ExtractBootBinaries(apkPath);

            Log?.Invoke("Binaries de Magisk extraídos correctamente.");
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Error descargando Magisk: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Extrae magiskboot, magiskinit y magisk{32,64} desde el APK de Magisk.
    ///
    /// En v24+ los binaries NO están en assets/ sino en lib/$ABI/lib*.so dentro del APK.
    /// Se renombran al extraer:
    ///   libmagiskboot.so  →  magiskboot  (o magiskboot32 en armeabi-v7a)
    ///   libmagiskinit.so  →  magiskinit
    ///   libmagisk.so      →  magisk64  (arm64-v8a/x86_64) o magisk32 (armeabi-v7a/x86)
    /// </summary>
    private void ExtractBootBinaries(string apkPath)
    {
        using var archive = ZipFile.OpenRead(apkPath);

        // Archivos objetivo dentro del APK (lib/$ABI/ y extensión .so)
        HashSet<string> wanted = new(StringComparer.OrdinalIgnoreCase)
        {
            "libmagiskboot.so",
            "libmagiskinit.so",
            "libmagisk.so",
        };

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            var parts = entry.FullName.Split('/');
            if (parts.Length < 2) continue;
            var fileName = parts[^1];
            if (!wanted.Contains(fileName)) continue;

            // extraer 'lib' + '.so'  →  nombre base
            var baseName = fileName.AsSpan(3, fileName.Length - 6).ToString();

            if (baseName is "magiskboot")
            {
                // magiskboot no cambia de nombre por ABI (se usa en el dispositivo para todas)
                WriteFile(entry, "magiskboot");
                found.Add("magiskboot");
            }
            else if (baseName is "magiskinit")
            {
                WriteFile(entry, "magiskinit");
                found.Add("magiskinit");
            }
            else if (baseName is "magisk")
            {
                // libmagisk.so → magisk64 o magisk32 según la ABI del directorio
                var abiDir = parts[1]; // "arm64-v8a", "armeabi-v7a", "x86_64", "x86"
                var is64 = abiDir.Contains("64") || abiDir.Contains("arm64");
                var destName = is64 ? "magisk64" : "magisk32";
                WriteFile(entry, destName);
                found.Add(destName);
            }
        }

        if (found.Count == 0)
        {
            Log?.Invoke("ADVERTENCIA: No se encontraron binaries en el APK.");
            Log?.Invoke("  La estructura del APK de Magisk pudo haber cambiado en esta versión.");
        }
        else
        {
            Log?.Invoke($"  Binarios extraídos: {string.Join(", ", found.OrderBy(n => n))}");
        }
    }

    private void WriteFile(ZipArchiveEntry entry, string destFileName)
    {
        var destPath = Path.Combine(MagiskDir, destFileName);
        using var outStream = File.Create(destPath);
        using var inStream = entry.Open();
        inStream.CopyTo(outStream);
        Log?.Invoke($"  Extraído: {entry.FullName} → {destFileName}");
    }

    /// <summary>
    /// Devuelve la ruta al binario solicitado para la ABI del dispositivo.
    /// </summary>
    public string? GetBinaryPath(string binaryName, string deviceAbi)
    {
        if (binaryName == "magiskboot")
        {
            // Preferir versión específica de la ABI
            if (AbiBootMap.TryGetValue(deviceAbi, out var bootName))
            {
                var specific = Path.Combine(MagiskDir, bootName);
                if (File.Exists(specific)) return specific;
            }

            // Fallbacks
            foreach (var fallback in new[] { "magiskboot", "magiskboot32" })
            {
                var p = Path.Combine(MagiskDir, fallback);
                if (File.Exists(p)) return p;
            }
        }

        if (binaryName == "magisk")
        {
            var is64Bit = deviceAbi.Contains("64", StringComparison.OrdinalIgnoreCase) ||
                          deviceAbi.Contains("arm64", StringComparison.OrdinalIgnoreCase) ||
                          deviceAbi == "x86_64";
            var name = is64Bit ? "magisk64" : "magisk32";
            var p = Path.Combine(MagiskDir, name);
            if (File.Exists(p)) return p;
        }

        if (binaryName == "magiskinit")
        {
            var p = Path.Combine(MagiskDir, "magiskinit");
            if (File.Exists(p)) return p;
        }

        return null;
    }

    public bool HasBinaries => Directory.Exists(MagiskDir) &&
        Directory.GetFiles(MagiskDir).Any(f =>
        {
            var n = Path.GetFileName(f);
            return n is "magiskboot" or "magiskboot32" or "magisk64" or "magisk32" or "magiskinit";
        });

    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(MagiskDir))
                Directory.Delete(MagiskDir, recursive: true);
        }
        catch { }
    }

    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }

    private class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }

    private static string CalculateSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
