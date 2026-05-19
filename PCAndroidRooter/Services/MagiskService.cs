using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
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

            Log?.Invoke("APK descargado. Extrayendo binaries...");
            ExtractBinaries(apkPath);

            Log?.Invoke("Binaries de Magisk extraídos correctamente.");
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Error descargando Magisk: {ex.Message}");
            return false;
        }
    }

    private void ExtractBinaries(string apkPath)
    {
        using var archive = ZipFile.OpenRead(apkPath);

        var binaryEntries = archive.Entries
            .Where(e => e.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) && !e.Name.EndsWith(".png"))
            .ToList();

        foreach (var entry in binaryEntries)
        {
            var relativePath = entry.FullName.Replace("assets/", "");
            var destPath = Path.Combine(MagiskDir, relativePath);
            var destDir = Path.GetDirectoryName(destPath);
            if (destDir != null) Directory.CreateDirectory(destDir);

            entry.ExtractToFile(destPath, overwrite: true);
            Log?.Invoke($"  Extraído: {relativePath}");
        }
    }

    public string? GetBinaryPath(string binaryName, string deviceAbi)
    {
        var is64Bit = deviceAbi.Contains("64", StringComparison.OrdinalIgnoreCase) ||
                      deviceAbi.Contains("arm64", StringComparison.OrdinalIgnoreCase) ||
                      deviceAbi == "x86_64";

        if (binaryName == "magiskboot")
        {
            var path = Path.Combine(MagiskDir, "magiskboot");
            if (File.Exists(path)) return path;

            path = Path.Combine(MagiskDir, "magiskboot32");
            if (File.Exists(path)) return path;
        }

        if (binaryName == "magisk")
        {
            var name = is64Bit ? "magisk64" : "magisk32";
            var path = Path.Combine(MagiskDir, name);
            if (File.Exists(path)) return path;
        }

        if (binaryName == "magiskinit")
        {
            var path = Path.Combine(MagiskDir, "magiskinit");
            if (File.Exists(path)) return path;
        }

        return null;
    }

    public bool HasBinaries => Directory.Exists(MagiskDir) &&
        Directory.GetFiles(MagiskDir).Any(f =>
            Path.GetFileName(f) is "magiskboot" or "magiskboot32" or "magisk64" or "magisk32");

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
    }

    private class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
