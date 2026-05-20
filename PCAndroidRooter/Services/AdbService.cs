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

public class AdbService
{
    private string _adbPath;
    private string _fastbootPath;
    private readonly HttpClient _httpClient;
    private const string AdbUrl = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";

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

    public async Task InitializeAsync()
    {
        if (!AdbExists)
        {
            await DownloadPlatformToolsAsync();
        }

        if (AdbExists)
        {
            await Task.Run(() => ExecuteAdb("start-server"));
        }
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

    public AdbCommandResult ExecuteAdb(string arguments, bool dispatchOutput = true, CancellationToken ct = default)
    {
        return ExecuteBinary(_adbPath, arguments, dispatchOutput, ct);
    }

     public AdbCommandResult ExecuteFastboot(string arguments, bool dispatchOutput = true, CancellationToken ct = default)
     {
         return ExecuteBinary(_fastbootPath, arguments, dispatchOutput, ct, Encoding.UTF8);
     }

     private AdbCommandResult ExecuteBinary(string binaryPath, string arguments, bool dispatchOutput = true, CancellationToken ct = default, Encoding? outputEncoding = null)
     {
         var result = new AdbCommandResult();

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

            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                result.Success = false;
                result.Error = "El comando excedió el tiempo límite (15s)";
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
         var result = ExecuteAdb("devices -l", dispatchOutput: false);
         if (!result.Success)
         {
             Debug.WriteLine($"[ADB] devices -l falló: {result.Error}");
             return new List<string>();
         }

         Debug.WriteLine($"[ADB] devices -l output: \"{result.Output}\"");

         var devices = new List<string>();
         var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines.Skip(1))
        {
            var trimmed = line.Trim();
            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var state = parts[1].TrimEnd('\r');
            if (state == "device" && !trimmed.Contains("unauthorized"))
            {
                var serial = parts[0];
                if (!string.IsNullOrEmpty(serial))
                    devices.Add(serial);
            }
        }

        return devices;
    }

    public List<string> GetFastbootDevices()
    {
        var result = ExecuteFastboot("devices", dispatchOutput: false);
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

        return info;
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
            ExecuteAdb($"-s {serial} shell su -c \"dd if={partition.Path} of={tempDevicePath} bs=1M 2>/dev/null\"", ct: ct), ct);

        if (!ddResult.Success && !ct.IsCancellationRequested)
            ddResult = await Task.Run(() =>
                ExecuteAdb($"-s {serial} shell \"dd if={partition.Path} of={tempDevicePath} bs=1M 2>/dev/null\"", ct: ct), ct);

        if (ddResult.Success && !ct.IsCancellationRequested)
        {
            var pullResult = await Task.Run(() =>
                ExecuteAdb($"-s {serial} pull {tempDevicePath} \"{outputPath}\"", ct: ct), ct);
            await Task.Run(() => ExecuteAdb($"-s {serial} shell \"rm -f {tempDevicePath}\""));

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
            process.WaitForExit(30000);

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

    public AdbCommandResult PushFile(string serial, string localPath, string remotePath)
    {
        return ExecuteAdb($"-s {serial} push \"{localPath}\" {remotePath}");
    }

    public AdbCommandResult PullFile(string serial, string remotePath, string localPath)
    {
        return ExecuteAdb($"-s {serial} pull {remotePath} \"{localPath}\"");
    }

    public AdbCommandResult Shell(string serial, string command)
    {
        return ExecuteAdb($"-s {serial} shell \"{command}\"");
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
            process.WaitForExit(30000);

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

        if (!string.IsNullOrEmpty(serial) && serial != "?")
        {
            var result = ExecuteFastboot($"-s {serial} flash boot \"{bootImgPath}\"");
            if (result.Success) return true;
            OutputReceived?.Invoke($"  falló con serial, reintentando sin serial...");
        }

        var result2 = ExecuteFastboot($"flash boot \"{bootImgPath}\"");
        return result2.Success;
    }

    public void KillAdb()
    {
        ExecuteAdb("kill-server");
    }

    public void RebootToBootloader(string serial)
    {
        ExecuteAdb($"-s {serial} reboot bootloader");
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
        ExecuteFastboot($"-s {serial} reboot");
    }

    public AdbCommandResult CheckBootloaderUnlock(string serial)
    {
        var result = ExecuteFastboot($"-s {serial} oem device-info 2>/dev/null");
        if (!result.Success)
            result = ExecuteFastboot($"-s {serial} getvar unlocked 2>/dev/null");
        return result;
    }

    public AdbCommandResult InstallApk(string serial, string apkPath)
    {
        return ExecuteAdb($"-s {serial} install -r \"{apkPath}\"");
    }
}
