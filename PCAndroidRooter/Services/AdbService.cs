 using System;
 using System.Collections.Generic;
 using System.ComponentModel;
 using System.Diagnostics;
 using System.IO;
 using System.IO.Compression;
 using System.Net.Http;
 using System.Text;
 using System.Text.RegularExpressions;
 using System.Windows;
 using PCAndroidRooter.Models;

namespace PCAndroidRooter.Services;

 public class AdbService : IDisposable
{
    private string _adbPath;
    private string _fastbootPath;
    private readonly HttpClient _httpClient;
    private bool _disposed;
    private const string AdbUrl = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";

    // Allowlist regex for serial numbers — only alphanumeric, dots, hyphens, colons, underscores
    private static readonly Regex ValidSerialRegex = new(@"^[a-zA-Z0-9\.\-_:]+$", RegexOptions.Compiled);
    // Allowlist regex for block device paths (nested by-name/bootdevice/platform allowed)
    private static readonly Regex ValidBlockPathRegex = new(
        @"^/dev/block/(?:by-name|bootdevice|platform)(?:/[\w\-\.]+)+$|^/dev/block/[\w\-\.]+$|^/dev/bootimg$",
        RegexOptions.Compiled);
    // Allowlist regex for package names
    private static readonly Regex ValidPackageNameRegex = new(@"^[a-zA-Z0-9._\-]+$", RegexOptions.Compiled);
    // Allowlist regex for general remote file paths (backup/restore): absolute, no shell metacharacters
    // Incluye '~' porque las rutas APK usan /data/app/~~xxxx==/pkg-yy==/base.apk
    private static readonly Regex ValidRemoteFilePathRegex = new(
        @"^/[A-Za-z0-9._\-=/+~ ]+$",
        RegexOptions.Compiled);

    /// <summary>
    /// Valida que un serial number sea seguro para usar en comandos ADB.
    /// </summary>
    public static bool IsValidSerial(string serial) =>
        !string.IsNullOrWhiteSpace(serial) && ValidSerialRegex.IsMatch(serial);

    /// <summary>
    /// Valida que un path de partición sea seguro para usar en comandos shell.
    /// Rechaza path traversal (..) y caracteres no permitidos.
    /// </summary>
    public static bool IsValidBlockPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.Contains("..", StringComparison.Ordinal) &&
        !path.Contains('\0') &&
        (ValidBlockPathRegex.IsMatch(path) || path.StartsWith("/data/local/tmp/", StringComparison.Ordinal));

    /// <summary>
    /// Valida una ruta remota absoluta para adb pull/push (backup/restore).
    /// Permite /data/app, /sdcard, /dev/block, etc. Rechaza path traversal,
    /// bytes nulos y metacaracteres de shell (; $ ` | & etc).
    /// </summary>
    public static bool IsValidRemoteFilePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.Contains("..", StringComparison.Ordinal) &&
        !path.Contains('\0') &&
        path.StartsWith("/", StringComparison.Ordinal) &&
        ValidRemoteFilePathRegex.IsMatch(path);

    /// <summary>
    /// Valida que un nombre de paquete Android sea seguro para usar en comandos shell.
    /// </summary>
    public static bool IsValidPackageName(string packageName) =>
        !string.IsNullOrWhiteSpace(packageName) && ValidPackageNameRegex.IsMatch(packageName);

    /// <summary>
    /// Valida que un filename sea seguro para operaciones de backup (sin path traversal).
    /// </summary>
    public static string SanitizeFileName(string fileName)
    {
        // Remove path separators and null bytes
        var sanitized = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(sanitized)) return "unknown";
        // Keep only safe characters
        return Regex.Replace(sanitized, @"[^a-zA-Z0-9\._\-]", "_");
    }

    /// <summary>
    /// Guard that throws if serial is not valid. Used before building ADB commands.
    /// </summary>
    private static void GuardValidSerial(string serial)
    {
        if (!IsValidSerial(serial))
            throw new ArgumentException($"Serial number inválido o potencialmente malicioso: '{serial}'");
    }

    public event Action<string>? OutputReceived;
    public event Action<string>? ErrorReceived;
    public event Action<double>? DownloadProgress;
    public event Action<string>? CommandExecuting;

    public AdbService()
    {
        var baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "platform-tools");
        _adbPath = Path.Combine(baseDir, "adb.exe");
        _fastbootPath = Path.Combine(baseDir, "fastboot.exe");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public bool AdbExists => File.Exists(_adbPath);

    public async Task<bool> InitializeAsync()
    {
        if (!AdbExists)
        {
            await DownloadPlatformToolsAsync();
        }

        if (AdbExists)
        {
            var result = await Task.Run(() => ExecuteAdb("start-server", dispatchOutput: false, timeoutMs: 10000));
            if (!result.Success)
            {
                ErrorReceived?.Invoke($"Error al iniciar ADB server: {result.Error}");
                return false;
            }

            // Verificar que ADB realmente funciona
            await Task.Delay(500);
            var checkResult = await Task.Run(() => ExecuteAdb("devices", dispatchOutput: false, timeoutMs: 5000));
            if (!checkResult.Success)
            {
                ErrorReceived?.Invoke($"ADB no responde después de iniciar: {checkResult.Error}");
                KillAdb();
                return false;
            }
        }
        else
        {
            ErrorReceived?.Invoke("ADB no encontrado después de la descarga.");
            return false;
        }

        return true;
    }

    private async Task DownloadPlatformToolsAsync()
    {
        OutputReceived?.Invoke("Descargando platform-tools de Android...");

        var extractDir = Path.GetDirectoryName(_adbPath)!;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var tempZip = Path.Combine(Path.GetTempPath(), $"platform-tools-{Guid.NewGuid():N}.zip");
            try
            {
                using var response = await _httpClient.GetAsync(AdbUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                using var stream = await response.Content.ReadAsStreamAsync();

                {
                    using var fileStream = File.Create(tempZip);
                    var buffer = new byte[8192];
                    long readBytes = 0;
                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                        readBytes += bytesRead;
                        if (totalBytes > 0)
                            DownloadProgress?.Invoke((double)readBytes / totalBytes);
                    }
                }

                // platform-tools-latest is a rolling URL: hash changes every release.
                // Integrity comes from HTTPS/TLS to dl.google.com; log hash for audit only.
                var downloadedHash = CalculateFileSha256(tempZip);
                OutputReceived?.Invoke($"  SHA256 del ZIP: {downloadedHash}");

                OutputReceived?.Invoke("Extrayendo platform-tools...");

                var baseExtractDir = AppDomain.CurrentDomain.BaseDirectory;
                if (Directory.Exists(extractDir))
                    Directory.Delete(extractDir, recursive: true);

                ZipFile.ExtractToDirectory(tempZip, baseExtractDir, overwriteFiles: true);

                _adbPath = Path.Combine(baseExtractDir, "platform-tools", "adb.exe");
                _fastbootPath = Path.Combine(baseExtractDir, "platform-tools", "fastboot.exe");

                OutputReceived?.Invoke("Platform-tools instalados correctamente.");
                return;
            }
            catch (Exception ex)
            {
                ErrorReceived?.Invoke($"Intento {attempt + 1} falló: {ex.Message}");
                if (attempt == 2)
                {
                    ErrorReceived?.Invoke("No se pudieron descargar los platform-tools después de 3 intentos.");
                    throw;
                }
                await Task.Delay(1000);
            }
            finally
            {
                if (File.Exists(tempZip))
                {
                    try { File.Delete(tempZip); } catch { }
                }
            }
        }
    }

    private static string CalculateFileSha256(string filePath)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    public AdbCommandResult ExecuteAdb(string arguments, bool dispatchOutput = true, CancellationToken ct = default, int timeoutMs = 15000)
    {
        return ExecuteBinary(_adbPath, arguments, dispatchOutput, ct, timeoutMs: timeoutMs);
    }

     public AdbCommandResult ExecuteFastboot(string arguments, bool dispatchOutput = true, CancellationToken ct = default, int timeoutMs = 15000)
     {
         return ExecuteBinary(_fastbootPath, arguments, dispatchOutput, ct, Encoding.UTF8, timeoutMs);
     }

     private AdbCommandResult ExecuteBinary(string binaryPath, string arguments, bool dispatchOutput = true, CancellationToken ct = default, Encoding? outputEncoding = null, int timeoutMs = 15000)
     {
         var result = new AdbCommandResult();

         // Guard: validate serial from arguments before executing
         var serialMatch = Regex.Match(arguments, @"-s\s+(\S+)");
         if (serialMatch.Success)
         {
             var serial = serialMatch.Groups[1].Value;
             GuardValidSerial(serial);
         }

         var binaryName = Path.GetFileNameWithoutExtension(binaryPath);
         CommandExecuting?.Invoke($"> {binaryName} {arguments}");

         try
         {
            if (ct.IsCancellationRequested)
            {
                result.Success = false;
                result.Error = "Operación cancelada";
                return result;
            }

             var psi = new ProcessStartInfo
             {
                 FileName = binaryPath,
                 Arguments = arguments,
                 UseShellExecute = false,
                 CreateNoWindow = true,
                 RedirectStandardOutput = true,
                 RedirectStandardError = true,
                 StandardOutputEncoding = outputEncoding ?? Encoding.UTF8,
                 StandardErrorEncoding = outputEncoding ?? Encoding.UTF8
             };

            using var process = Process.Start(psi);
            if (process == null)
            {
                result.Success = false;
                result.Error = "No se pudo iniciar el proceso";
                return result;
            }

            var output = new StringBuilder();
            var error = new StringBuilder();

            using var outputWaitHandle = new AutoResetEvent(false);
            using var errorWaitHandle = new AutoResetEvent(false);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) outputWaitHandle.Set();
                else
                {
                    output.AppendLine(e.Data);
                    if (dispatchOutput)
                        Application.Current?.Dispatcher.InvokeAsync(() => OutputReceived?.Invoke(e.Data));
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) errorWaitHandle.Set();
                else
                {
                    var clean = StripAnsi(e.Data);
                    error.AppendLine(e.Data);
                    if (dispatchOutput)
                    {
                        if (clean.StartsWith("* daemon not running; starting now at tcp:", StringComparison.Ordinal) ||
                            clean.StartsWith("* daemon started successfully", StringComparison.Ordinal) ||
                            clean.StartsWith("daemon started successfully", StringComparison.Ordinal))
                            Application.Current?.Dispatcher.InvokeAsync(() => OutputReceived?.Invoke(e.Data));
                        else
                            Application.Current?.Dispatcher.InvokeAsync(() => ErrorReceived?.Invoke(e.Data));
                    }
                }
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeoutStr = timeoutMs >= 60000 ? $"{timeoutMs / 1000}s" : $"{timeoutMs}ms";
            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    // Verify process actually died
                    if (!process.WaitForExit(3000))
                    {
                        result.Success = false;
                        result.Error = $"El proceso no pudo ser terminado ({binaryName})";
                        return result;
                    }
                }
                catch (InvalidOperationException) { } // Already exited
                catch { }
                result.Success = false;
                result.Error = $"El comando excedió el tiempo límite ({timeoutStr})";
                return result;
            }

            outputWaitHandle.WaitOne(5000);
            errorWaitHandle.WaitOne(5000);

            result.Success = process.ExitCode == 0;
            result.Output = output.ToString().TrimEnd();
            result.Error = error.ToString().TrimEnd();
            result.ExitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    // Elimina códigos de escape ANSI (colores, negrita, etc.) de la salida de procesos
    private static string StripAnsi(string input)
    {
        return Regex.Replace(input, @"\x1B\[[0-9;]*[mK]", string.Empty);
    }

    public List<string> GetConnectedDevices()
     {
         var result = ExecuteAdb("devices -l", dispatchOutput: false, timeoutMs: 5000);
         if (!result.Success)
         {
             Debug.WriteLine($"[ADB] devices -l falló: {result.Error}");
             return new List<string>();
         }

         var devices = new List<string>();
         var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
         bool foundUnauthorized = false;
         bool foundOffline = false;

        foreach (var line in lines.Skip(1))
        {
            var trimmed = line.Trim();
            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var state = parts[1].TrimEnd('\r');
            var serial = parts[0];

            if (state == "unauthorized")
            {
                foundUnauthorized = true;
                continue;
            }
            if (state == "offline")
            {
                foundOffline = true;
                continue;
            }
            if (state == "device")
            {
                if (!string.IsNullOrEmpty(serial))
                    devices.Add(serial);
            }
        }

        if (foundUnauthorized && devices.Count == 0)
            OutputReceived?.Invoke("⚠ Dispositivo detectado pero NO AUTORIZADO. Revisa la pantalla del teléfono y acepta la solicitud de depuración USB.");
        if (foundOffline && devices.Count == 0)
            OutputReceived?.Invoke("⚠ Dispositivo detectado pero OFFLINE. Prueba a reconectar el cable USB.");

        var hasEntries = lines.Length > 1;
        if (!hasEntries && !foundUnauthorized && !foundOffline)
        {
            OutputReceived?.Invoke("ℹ No se detectan dispositivos. Asegúrate de:");
            OutputReceived?.Invoke("   1. Depuración USB activada en Opciones de desarrollador");
            OutputReceived?.Invoke("   2. Aceptar la huella RSA en la pantalla del teléfono");
            OutputReceived?.Invoke("   3. Tener el driver USB del fabricante instalado en el PC");
            OutputReceived?.Invoke("   4. Probar con otro cable USB o puerto USB 2.0");
        }

        return devices;
    }

    public List<string> GetFastbootDevices()
    {
        var result = ExecuteFastboot("devices", dispatchOutput: false, timeoutMs: 5000);
        if (!result.Success) return new List<string>();

        var devices = new List<string>();
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (line.Trim().Contains("fastboot"))
            {
                var serial = line.Split('\t', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(serial))
                    devices.Add(serial.Trim());
            }
        }

        return devices;
    }

    public async Task<DeviceInfo?> GetDeviceInfoAsync(string serial)
    {
        var info = new DeviceInfo { Serial = serial, ConnectionStatus = "Conectado" };

        var props = new Dictionary<string, Action<string, DeviceInfo>>
        {
            ["ro.product.model"] = (v, d) => d.Model = v,
            ["ro.product.manufacturer"] = (v, d) => d.Manufacturer = v,
            ["ro.build.version.release"] = (v, d) => d.AndroidVersion = v,
            ["ro.build.display.id"] = (v, d) => d.BuildNumber = v,
            ["ro.product.name"] = (v, d) => d.ProductName = v,
            ["ro.build.version.security_patch"] = (v, d) => d.SecurityPatch = v,
            ["ro.product.cpu.abi"] = (v, d) => d.Abi = v,
        };

        foreach (var (prop, setter) in props)
        {
            var result = await Task.Run(() =>
                ExecuteAdb($"-s {serial} shell getprop {prop}"));
            if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
                setter(result.Output.Trim(), info);
        }

        var batteryResult = await Task.Run(() =>
            ExecuteAdb($"-s {serial} shell dumpsys battery"));
        if (batteryResult.Success)
        {
            foreach (var line in batteryResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Trim().StartsWith("level:", StringComparison.OrdinalIgnoreCase))
                {
                    var match = Regex.Match(line, @"\d+");
                    if (match.Success) info.BatteryLevel = $"{match.Value}%";
                    break;
                }
            }
        }

        var isRooted = false;

        var whichSu = await Task.Run(() =>
            ExecuteAdb($"-s {serial} shell which su"));
        if (whichSu.Success && !string.IsNullOrWhiteSpace(whichSu.Output))
        {
            var suTest = await Task.Run(() =>
                ExecuteAdb($"-s {serial} shell su -c id"));
            isRooted = suTest.Success && suTest.Output.Contains("uid=0");
        }

        if (!isRooted)
        {
            var magiskCheck = await Task.Run(() =>
                ExecuteAdb($"-s {serial} shell pm list packages 2>/dev/null | grep -i magisk"));
            if (magiskCheck.Success && !string.IsNullOrWhiteSpace(magiskCheck.Output))
                isRooted = true;
        }

        if (!isRooted)
        {
            var adbRoot = await Task.Run(() =>
                ExecuteAdb($"-s {serial} root"));
            if (adbRoot.Success && adbRoot.Output.Contains("adbd is already running as root"))
                isRooted = true;
        }

        info.IsRooted = isRooted;

        var bootloaderResult = await Task.Run(() =>
            ExecuteAdb($"-s {serial} shell getprop ro.boot.flash.locked"));
        var blUnlocked = bootloaderResult.Success && bootloaderResult.Output.Trim() == "0";
        if (!blUnlocked)
        {
            var oemResult = await Task.Run(() =>
                ExecuteAdb($"-s {serial} shell getprop ro.oem_unlock_supported"));
            if (oemResult.Success && oemResult.Output.Trim() == "1")
            {
                var unlockResult = await Task.Run(() =>
                    ExecuteAdb($"-s {serial} shell getprop sys.oem_unlock_allowed"));
                blUnlocked = unlockResult.Success && unlockResult.Output.Trim() == "1";
            }
            else
            {
                var otherLocked = await Task.Run(() =>
                    ExecuteAdb($"-s {serial} shell getprop ro.boot.other.locked"));
                if (otherLocked.Success && otherLocked.Output.Trim() == "0")
                    blUnlocked = true;
            }
        }
        info.BootloaderUnlocked = blUnlocked;

        var ramResult = await Task.Run(() =>
                ExecuteAdb($"-s {serial} shell cat /proc/meminfo"));
            if (ramResult.Success)
            {
                foreach (var line in ramResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Trim().StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                    {
                        var match = Regex.Match(line, @"\d+");
                        if (match.Success && long.TryParse(match.Value, out var kb))
                            info.TotalRam = kb / 1024;
                        break;
                    }
                }
            }

            info.IsMediaTek = await DetectIsMediaTekAsync(serial);

            var bootmode = await Task.Run(() =>
                ExecuteAdb($"-s {serial} shell getprop ro.bootmode", dispatchOutput: false, timeoutMs: 5000));
            var mode = bootmode.Success ? bootmode.Output.Trim().ToLowerInvariant() : "";
            info.IsRecovery = mode is "recovery" or "recovery2";
            info.IsFastboot = mode is "bootloader" or "fastboot";

            return info;
        }

        /// <summary>
        /// Detects MediaTek SoC from board/hardware props (not ABI — ABI is arm64 on almost every phone).
        /// </summary>
        public async Task<bool> DetectIsMediaTekAsync(string serial)
        {
            GuardValidSerial(serial);
            var props = new[] { "ro.board.platform", "ro.hardware", "ro.mediatek.platform", "ro.product.board" };
            foreach (var prop in props)
            {
                var result = await Task.Run(() =>
                    ExecuteAdb($"-s {serial} shell getprop {prop}", dispatchOutput: false, timeoutMs: 5000));
                if (!result.Success) continue;
                var v = result.Output.Trim().ToLowerInvariant();
                if (v.Contains("mediatek") || v.Contains("mt6") || v.Contains("mt8") ||
                    Regex.IsMatch(v, @"^mt\d{4}"))
                    return true;
            }
            return false;
        }

    public async Task<BootPartitionInfo?> FindBootPartitionAsync(string serial, CancellationToken ct = default)
    {
        var possiblePaths = new[]
        {
            "/dev/block/by-name/boot_a",
            "/dev/block/by-name/boot_b",
            "/dev/block/by-name/boot",
            "/dev/block/bootdevice/by-name/boot",
            "/dev/block/platform/*/by-name/boot",
            "/dev/block/boot",
            "/dev/bootimg"
        };

        foreach (var pathTemplate in possiblePaths)
        {
            if (pathTemplate.Contains('*'))
            {
                var findResult = await Task.Run(() =>
                    ExecuteAdb($"-s {serial} shell \"ls {pathTemplate} 2>/dev/null\"", ct: ct), ct);
                if (findResult.Success && !ct.IsCancellationRequested)
                {
                    var lines = findResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (ct.IsCancellationRequested) return null;
                        var realPath = line.Trim();
                        if (!string.IsNullOrEmpty(realPath))
                        {
                            var checkResult = await Task.Run(() =>
                                ExecuteAdb($"-s {serial} shell \"ls -l {realPath} 2>/dev/null\"", ct: ct), ct);
                            if (checkResult.Success && !ct.IsCancellationRequested)
                            {
                                var sizeResult = await Task.Run(() =>
                                    ExecuteAdb($"-s {serial} shell \"wc -c < {realPath} 2>/dev/null\""));
                                long.TryParse(sizeResult.Output.Trim(), out var size);

                                return new BootPartitionInfo
                                {
                                    Path = realPath,
                                    Size = size,
                                    Accessible = true,
                                    BlockDevice = realPath
                                };
                            }
                        }
                    }
                }
            }
            else
            {
                if (ct.IsCancellationRequested) return null;
                var checkResult = await Task.Run(() =>
                    ExecuteAdb($"-s {serial} shell \"ls -l {pathTemplate} 2>/dev/null\"", ct: ct), ct);
                if (checkResult.Success && !ct.IsCancellationRequested)
                {
                    var realPath = checkResult.Output.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                        .LastOrDefault() ?? pathTemplate;

                    var sizeResult = await Task.Run(() =>
                        ExecuteAdb($"-s {serial} shell \"wc -c < {pathTemplate} 2>/dev/null\"", ct: ct), ct);
                    long.TryParse(sizeResult.Output.Trim(), out var size);

                    return new BootPartitionInfo
                    {
                        Path = pathTemplate,
                        Size = size,
                        Accessible = true,
                        BlockDevice = realPath
                    };
                }
            }
        }

        OutputReceived?.Invoke("No se encontró la partición boot automáticamente.");
        return null;
    }

    public async Task<bool> ExtractBootImgAsync(string serial, BootPartitionInfo partition, string outputPath, CancellationToken ct = default)
    {
        OutputReceived?.Invoke($"Extrayendo boot.img desde {partition.Path} ({partition.Size / 1024 / 1024} MB)...");

        var tempDevicePath = "/data/local/tmp/boot.img";

        var ddResult = await Task.Run(() =>
            ExecuteAdb($"-s {serial} shell su -c \"dd if={partition.Path} of={tempDevicePath} bs=1M 2>/dev/null\"", ct: ct, timeoutMs: 120000), ct);

        if (!ddResult.Success && !ct.IsCancellationRequested)
            ddResult = await Task.Run(() =>
                ExecuteAdb($"-s {serial} shell \"dd if={partition.Path} of={tempDevicePath} bs=1M 2>/dev/null\"", ct: ct, timeoutMs: 120000), ct);

        if (ddResult.Success && !ct.IsCancellationRequested)
        {
            var pullResult = await Task.Run(() =>
                ExecuteAdb($"-s {serial} pull {tempDevicePath} \"{outputPath}\"", ct: ct, timeoutMs: 120000), ct);
            await Task.Run(() => ExecuteAdb($"-s {serial} shell \"rm -f {tempDevicePath}\"", timeoutMs: 15000));

            if (pullResult.Success && File.Exists(outputPath))
            {
                var fileInfo = new FileInfo(outputPath);
                OutputReceived?.Invoke($"Boot.img extraído: {outputPath} ({fileInfo.Length / 1024 / 1024} MB)");
                return true;
            }
        }

        if (ct.IsCancellationRequested) return false;

        OutputReceived?.Invoke("Usando método exec-out directo (raw binary)...");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
                Arguments = $"-s {serial} exec-out \"cat {partition.Path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            using var outputFile = File.Create(outputPath);
            await process.StandardOutput.BaseStream.CopyToAsync(outputFile, 81920, ct);

            var error = await process.StandardError.ReadToEndAsync(ct);
            process.WaitForExit(120000);

            if (process.ExitCode == 0 && new FileInfo(outputPath).Length > 100000)
            {
                var fileInfo = new FileInfo(outputPath);
                OutputReceived?.Invoke($"Boot.img extraído vía exec-out: {outputPath} ({fileInfo.Length / 1024 / 1024} MB)");
                return true;
            }

            if (!string.IsNullOrEmpty(error))
                OutputReceived?.Invoke($"Error: {error}");
        }
        catch (OperationCanceledException)
        {
            OutputReceived?.Invoke("Extracción cancelada por el usuario.");
            return false;
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke($"Error en exec-out: {ex.Message}");
        }

        OutputReceived?.Invoke("ERROR: No se pudo extraer boot.img.");
        OutputReceived?.Invoke("El dispositivo puede tener el bootloader bloqueado o partición no legible.");
        return false;
    }

    public AdbCommandResult PushFile(string serial, string localPath, string remotePath, int timeoutMs = 120000)
    {
        GuardValidSerial(serial);
        if (!IsValidRemoteFilePath(remotePath))
        {
            OutputReceived?.Invoke($"ERROR: Path remoto inválido: {remotePath}");
            return new AdbCommandResult { Success = false, Error = "Path remoto no permitido" };
        }
        return ExecuteAdb($"-s {serial} push \"{localPath}\" \"{remotePath}\"", timeoutMs: timeoutMs);
    }

    public AdbCommandResult PullFile(string serial, string remotePath, string localPath, int timeoutMs = 120000)
    {
        GuardValidSerial(serial);
        if (!IsValidRemoteFilePath(remotePath))
        {
            OutputReceived?.Invoke($"ERROR: Path remoto inválido: {remotePath}");
            return new AdbCommandResult { Success = false, Error = "Path remoto no permitido" };
        }
        return ExecuteAdb($"-s {serial} pull \"{remotePath}\" \"{localPath}\"", timeoutMs: timeoutMs);
    }

    public AdbCommandResult Shell(string serial, string command, int timeoutMs = 15000, CancellationToken ct = default)
    {
        GuardValidSerial(serial);
        return ExecuteAdb($"-s {serial} shell \"{command}\"", ct: ct, timeoutMs: timeoutMs);
    }

    public async Task<bool> ExecuteAdbRawAsync(string serial, string remotePath, string localPath, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
                Arguments = $"-s {serial} exec-out \"cat {remotePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            using var outputFile = File.Create(localPath);
            await process.StandardOutput.BaseStream.CopyToAsync(outputFile, 81920, ct);

            var error = await process.StandardError.ReadToEndAsync(ct);
            process.WaitForExit(120000);

            if (process.ExitCode == 0 && new FileInfo(localPath).Length > 100000)
                return true;

            if (!string.IsNullOrEmpty(error))
                OutputReceived?.Invoke($"Error: {error}");
            return false;
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke($"Error en exec-out raw: {ex.Message}");
            return false;
        }
    }

    public bool WaitForFastbootDevice(string serial, int timeoutSeconds = 30, CancellationToken ct = default)
    {
        OutputReceived?.Invoke("Esperando dispositivo en modo fastboot...");

        // Matar servidor ADB para evitar que comandos ADB cuelguen mientras el dispositivo está en fastboot
        ExecuteAdb("kill-server", dispatchOutput: false, timeoutMs: 3000);

        for (int i = 0; i < timeoutSeconds; i++)
        {
            if (ct.WaitHandle.WaitOne(1000) || ct.IsCancellationRequested)
            {
                OutputReceived?.Invoke("Espera cancelada por el usuario.");
                return false;
            }
            var devices = GetFastbootDevices();
            if (devices.Contains(serial) || devices.Contains("?"))
                return true;
        }
        return false;
    }

    public bool FlashBootViaFastboot(string serial, string bootImgPath)
    {
        OutputReceived?.Invoke($"Flasheando {bootImgPath} vía fastboot...");

        if (string.IsNullOrEmpty(serial) || serial == "?")
        {
            OutputReceived?.Invoke("ERROR: No se puede flashear sin serial de dispositivo identificado.");
            return false;
        }

        if (!IsValidSerial(serial))
        {
            OutputReceived?.Invoke("ERROR: Serial de dispositivo inválido.");
            return false;
        }

        var result = ExecuteFastboot($"-s {serial} flash boot \"{bootImgPath}\"", timeoutMs: 120000);
        return result.Success;
    }

    public bool CheckAdbHealthy()
    {
        var result = ExecuteAdb("devices", dispatchOutput: false, timeoutMs: 5000);
        return result.Success;
    }

    public string? DiagnoseAdbIssue()
    {
        var result = ExecuteAdb("devices", dispatchOutput: false, timeoutMs: 5000);
        if (result.Success)
            return null;

        if (result.Error.Contains("excedió el tiempo", StringComparison.OrdinalIgnoreCase)
            || result.Error.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "ADB no responde. El servidor puede estar bloqueado. Usa 'Reiniciar ADB'.";

        if (result.Error.Contains("cannot connect to daemon", StringComparison.OrdinalIgnoreCase))
            return "ADB no pudo conectar con el servidor. Prueba a reiniciar ADB.";

        return $"ADB error: {result.Error}";
    }

    public void KillAdb()
    {
        ExecuteAdb("kill-server", dispatchOutput: false, timeoutMs: 3000);
    }

    public async Task RestartAdbAsync()
    {
        KillAdb();
        await Task.Delay(500);
        await Task.Run(() => ExecuteAdb("start-server", dispatchOutput: false, timeoutMs: 5000));
    }

    public void RestartAdb()
    {
        KillAdb();
        Task.Delay(500).Wait();
        ExecuteAdb("start-server", dispatchOutput: false, timeoutMs: 5000);
    }

    public void RebootToBootloader(string serial)
    {
        OutputReceived?.Invoke("Enviando comando reboot bootloader...");
        var result = ExecuteAdb($"-s {serial} reboot bootloader", timeoutMs: 8000);
        if (!result.Success)
        {
            OutputReceived?.Invoke("  ADB ya no responde (esperado: el dispositivo está reiniciando).");
        }
    }

    public void RebootToRecovery(string serial)
    {
        ExecuteAdb($"-s {serial} reboot recovery");
    }

    public void RebootDevice(string serial)
    {
        ExecuteAdb($"-s {serial} reboot");
    }

    public void FastbootReboot(string serial)
    {
        ExecuteFastboot($"-s {serial} reboot", timeoutMs: 30000);
    }

    public AdbCommandResult CheckBootloaderUnlock(string serial)
    {
        // 1) Si el dispositivo está en Android (ADB): props estándar de estado.
        //    Antes solo se consultaba fastboot → con el device en ADB siempre daba "bloqueado".
        var adbDevices = GetConnectedDevices();
        if (adbDevices.Contains(serial))
        {
            var state = ExecuteAdb($"-s {serial} shell getprop ro.boot.vbmeta.device_state", timeoutMs: 8000);
            var stateVal = state.Success ? state.Output.Trim() : "";
            if (stateVal.Equals("unlocked", StringComparison.OrdinalIgnoreCase))
                return new AdbCommandResult { Success = true, Output = "unlocked: yes (adb ro.boot.vbmeta.device_state=unlocked)" };
            if (stateVal.Equals("locked", StringComparison.OrdinalIgnoreCase))
                return new AdbCommandResult { Success = true, Output = "unlocked: no (adb ro.boot.vbmeta.device_state=locked)" };

            var flashLocked = ExecuteAdb($"-s {serial} shell getprop ro.boot.flash.locked", timeoutMs: 8000);
            var flVal = flashLocked.Success ? flashLocked.Output.Trim() : "";
            if (flVal == "0")
                return new AdbCommandResult { Success = true, Output = "unlocked: yes (adb ro.boot.flash.locked=0)" };
            if (flVal == "1")
                return new AdbCommandResult { Success = true, Output = "unlocked: no (adb ro.boot.flash.locked=1)" };

            var vbs = ExecuteAdb($"-s {serial} shell getprop ro.boot.verifiedbootstate", timeoutMs: 8000);
            var vbsVal = vbs.Success ? vbs.Output.Trim() : "";
            if (vbsVal.Equals("orange", StringComparison.OrdinalIgnoreCase))
                return new AdbCommandResult { Success = true, Output = "unlocked: yes (adb ro.boot.verifiedbootstate=orange)" };
            if (vbsVal.Equals("green", StringComparison.OrdinalIgnoreCase))
                return new AdbCommandResult { Success = true, Output = "unlocked: no (adb ro.boot.verifiedbootstate=green)" };

            // Device presente en ADB pero sin props diagnósticas → no intentar fastboot (bloquearía 60s).
            return new AdbCommandResult { Success = true, Output = "unlocked: unknown (sin props de bootloader)" };
        }

        // 2) El dispositivo está en fastboot (u otro modo).
        //    Sin '2>/dev/null': fastboot no es un shell y ese token rompe/ignora el comando.
        var result = ExecuteFastboot($"-s {serial} oem device-info", timeoutMs: 30000);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            result = ExecuteFastboot($"-s {serial} getvar unlocked", timeoutMs: 30000);

        // fastboot escribe getvar/oem info en stderr en muchas versiones → combinar para el caller.
        var combined = $"{result.Output}\n{result.Error}".Trim();
        if (!string.IsNullOrWhiteSpace(combined))
            result.Output = combined;
        return result;
    }

    /// <summary>
    /// Interpreta la salida de CheckBootloaderUnlock (ADB o fastboot).
    /// Devuelve null si el estado es desconocido.
    /// </summary>
    public static bool? ParseBootloaderUnlocked(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var o = output.ToLowerInvariant();

        if (o.Contains("unlocked: yes") || o.Contains("unlocked: true") ||
            o.Contains("device unlocked: true") || o.Contains("device unlocked: yes") ||
            o.Contains("already unlocked") ||
            o.Contains("device_state=unlocked") ||
            o.Contains("verifiedbootstate=orange") ||
            o.Contains("flash.locked=0") ||
            (o.Contains("unlocked!") && !o.Contains("not unlocked")))
            return true;

        if (o.Contains("unlocked: no") || o.Contains("unlocked: false") ||
            o.Contains("device unlocked: false") || o.Contains("device unlocked: no") ||
            o.Contains("device_state=locked") ||
            o.Contains("verifiedbootstate=green") ||
            o.Contains("flash.locked=1") ||
            o.Contains("locked: yes"))
            return false;

        return null;
    }

    public AdbCommandResult InstallApk(string serial, string apkPath)
    {
        return ExecuteAdb($"-s {serial} install -r \"{apkPath}\"", timeoutMs: 60000);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { KillAdb(); } catch { }
        _httpClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}
