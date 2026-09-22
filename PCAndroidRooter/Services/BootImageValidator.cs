using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PCAndroidRooter.Services;

/// <summary>
/// Valida la integridad y compatibilidad de boot.img antes y después del parche Magisk.
/// </summary>
public static class BootImageValidator
{
    // Android boot image magic bytes: "ANDROID!" (0x414E44524F494421)
    private static readonly byte[] AndroidBootMagic = Encoding.ASCII.GetBytes("ANDROID!");

    // Android boot image v0/v1/v2 headers
    private const int BootMagicOffset = 0;
    public const int BootImageMinSize = 1024 * 1024;     // 1 MB mínimo
    private const int BootImageMaxSize = 256 * 1024 * 1024; // 256 MB máximo
    private const double MaxPatchedRatio = 1.5;  // Parcheado no puede ser >150% del original
    private const double MinPatchedRatio = 0.5;  // Parcheado no puede ser <50% del original

    public enum ValidationStatus
    {
        Valid,
        TooSmall,
        TooLarge,
        InvalidMagic,
        CorruptHeader,
        SizeMismatch,
        MagiskNotFound,
        HashMismatch,
        UnknownError
    }

    public class ValidationResult
    {
        public ValidationStatus Status { get; set; }
        public string Message { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string? Sha256 { get; set; }
        public bool ContainsMagisk { get; set; }
        public string? PartitionSlot { get; set; }
    }

    /// <summary>
    /// Valida un boot.img original extraído del dispositivo.
    /// </summary>
    public static ValidationResult ValidateOriginal(string filePath)
    {
        var result = new ValidationResult();

        if (!File.Exists(filePath))
        {
            result.Status = ValidationStatus.UnknownError;
            result.Message = "El archivo no existe.";
            return result;
        }

        var fileInfo = new FileInfo(filePath);
        result.FileSize = fileInfo.Length;

        // Tamaño mínimo
        if (fileInfo.Length < BootImageMinSize)
        {
            result.Status = ValidationStatus.TooSmall;
            result.Message = $"Archivo demasiado pequeño ({fileInfo.Length / 1024} KB). " +
                           $"Mínimo esperado: {BootImageMinSize / 1024 / 1024} MB.";
            return result;
        }

        // Tamaño máximo
        if (fileInfo.Length > BootImageMaxSize)
        {
            result.Status = ValidationStatus.TooLarge;
            result.Message = $"Archivo excesivamente grande ({fileInfo.Length / 1024 / 1024} MB). " +
                           $"Máximo esperado: {BootImageMaxSize / 1024 / 1024} MB.";
            return result;
        }

        // Verificar magic bytes del Android boot image header
        try
        {
            using var fs = File.OpenRead(filePath);
            var magic = ReadExact(fs, 8);
            if (magic == null)
            {
                result.Status = ValidationStatus.CorruptHeader;
                result.Message = "No se pudieron leer los primeros 8 bytes del archivo.";
                return result;
            }

            if (!magic.SequenceEqual(AndroidBootMagic))
            {
                result.Status = ValidationStatus.InvalidMagic;
                var magicHex = BitConverter.ToString(magic).Replace("-", " ");
                result.Message = $"Magic bytes inválidos: [{magicHex}]. " +
                               $"Esperado: [41 4E 44 52 4F 49 44 21] (ANDROID!). " +
                               "Este no parece ser un boot.img de Android válido.";
                return result;
            }
        }
        catch (Exception ex)
        {
            result.Status = ValidationStatus.CorruptHeader;
            result.Message = $"Error leyendo header: {ex.Message}";
            return result;
        }

        // Calcular SHA256
        result.Sha256 = CalculateSha256(filePath);
        result.Status = ValidationStatus.Valid;
        result.Message = $"boot.img válido. Magic: OK. Tamaño: {fileInfo.Length / 1024 / 1024} MB. SHA256: {result.Sha256[..16]}...";
        return result;
    }

    /// <summary>
    /// Valida un boot.img parcheado con Magisk.
    /// </summary>
    public static ValidationResult ValidatePatched(string patchedPath, string? originalPath = null)
    {
        var result = new ValidationResult();

        if (!File.Exists(patchedPath))
        {
            result.Status = ValidationStatus.UnknownError;
            result.Message = "El archivo parcheado no existe.";
            return result;
        }

        var fileInfo = new FileInfo(patchedPath);
        result.FileSize = fileInfo.Length;

        // Tamaño mínimo
        if (fileInfo.Length < BootImageMinSize)
        {
            result.Status = ValidationStatus.TooSmall;
            result.Message = $"Boot.img parcheado demasiado pequeño ({fileInfo.Length / 1024} KB). " +
                           "El parche de Magisk probablemente falló. No se flasheará.";
            return result;
        }

        // Tamaño máximo
        if (fileInfo.Length > BootImageMaxSize)
        {
            result.Status = ValidationStatus.TooLarge;
            result.Message = $"Boot.img parcheado excesivamente grande ({fileInfo.Length / 1024 / 1024} MB). " +
                           "Esto indica un error en el parche. No se flasheará.";
            return result;
        }

        // Verificar magic bytes
        try
        {
            using var fs = File.OpenRead(patchedPath);
            var magic = ReadExact(fs, 8);
            if (magic == null)
            {
                result.Status = ValidationStatus.CorruptHeader;
                result.Message = "No se pudieron leer los magic bytes del parcheado.";
                return result;
            }

            if (!magic.SequenceEqual(AndroidBootMagic))
            {
                result.Status = ValidationStatus.InvalidMagic;
                result.Message = "El boot.img parcheado no tiene magic bytes Android válido. " +
                               "El parche corrompió el archivo. No se flasheará.";
                return result;
            }

            // Verificar si contiene evidencia de Magisk (buscamos strings conocidos)
            fs.Seek(0, SeekOrigin.Begin);
            var content = new byte[Math.Min(fileInfo.Length, 1024 * 1024)]; // Leer primeros 1 MB
            int totalRead = 0;
            while (totalRead < content.Length)
            {
                int read = fs.Read(content, totalRead, content.Length - totalRead);
                if (read == 0) break;
                totalRead += read;
            }

            // Buscar firmas de Magisk en el binario
            result.ContainsMagisk = ContainsMagiskSignature(content);
        }
        catch (Exception ex)
        {
            result.Status = ValidationStatus.CorruptHeader;
            result.Message = $"Error validando parche: {ex.Message}";
            return result;
        }

        // Comparar con original si se proporciona
        if (!string.IsNullOrEmpty(originalPath) && File.Exists(originalPath))
        {
            var originalSize = new FileInfo(originalPath).Length;
            var ratio = (double)fileInfo.Length / originalSize;

            if (ratio > MaxPatchedRatio)
            {
                result.Status = ValidationStatus.SizeMismatch;
                result.Message = $"Boot.img parcheado es {ratio:P1} más grande que el original. " +
                               $"Original: {originalSize / 1024 / 1024} MB, Parcheado: {fileInfo.Length / 1024 / 1024} MB. " +
                               "Esto es inusual. No se flasheará.";
                return result;
            }

            if (ratio < MinPatchedRatio)
            {
                result.Status = ValidationStatus.SizeMismatch;
                result.Message = $"Boot.img parcheado es solo {ratio:P1} del tamaño original. " +
                               "El archivo puede estar truncado. No se flasheará.";
                return result;
            }

            // Verificar que los magic bytes del original y parcheado coincidan
            // (si el parche los cambió, algo está muy mal)
            try
            {
                using var origFs = File.OpenRead(originalPath);
                var origMagic = ReadExact(origFs, 8);

                using var patchFs = File.OpenRead(patchedPath);
                var patchMagic = ReadExact(patchFs, 8);

                if (origMagic != null && patchMagic != null && !origMagic.SequenceEqual(patchMagic))
                {
                    result.Status = ValidationStatus.CorruptHeader;
                    result.Message = "Los magic bytes del boot.img original y parcheado no coinciden. " +
                                   "El parche corrompió la estructura del archivo.";
                    return result;
                }
            }
            catch { }
        }

        // Calcular SHA256
        result.Sha256 = CalculateSha256(patchedPath);

        result.Status = ValidationStatus.Valid;
        result.Message = result.ContainsMagisk
            ? $"Boot.img parcheado VÁLIDO. Contiene firmas Magisk. SHA256: {result.Sha256[..16]}..."
            : $"Boot.img parcheado válido (sin firmas Magisk detectables). SHA256: {result.Sha256[..16]}...";
        return result;
    }

    /// <summary>
    /// Detecta la partición boot activa en dispositivos A/B.
    /// </summary>
    public static string? DetectActiveBootSlot(string serial, Func<string, (bool Success, string Output)> shellExecutor)
    {
        // Intentar detectar slot activo via getprop
        var slotResult = shellExecutor("getprop ro.boot.slot_suffix");
        if (slotResult.Success && !string.IsNullOrWhiteSpace(slotResult.Output))
        {
            var slot = slotResult.Output.Trim();
            if (slot == "_a" || slot == "_b")
                return slot;
        }

        // Verificar qué particiones existen
        var bootA = shellExecutor("ls /dev/block/by-name/boot_a 2>/dev/null");
        var bootB = shellExecutor("ls /dev/block/by-name/boot_b 2>/dev/null");

        if (bootA.Success && !bootA.Output.Contains("No such file"))
            return "_a";
        if (bootB.Success && !bootB.Output.Contains("No such file"))
            return "_b";

        return null; // Sistema A/B no detectado o partición unique
    }

    /// <summary>
    /// Verifica si el dispositivo tiene root real funcionando.
    /// </summary>
    public static bool VerifyRoot(string serial, Func<string, bool, int, (bool Success, string Output, string Error)> adbExecutor)
    {
        // Test 1: which su
        var whichSu = adbExecutor($"-s {serial} shell which su", false, 5000);
        if (whichSu.Success && !string.IsNullOrWhiteSpace(whichSu.Output) && !whichSu.Output.Contains("not found"))
        {
            // Test 2: su -c id
            var suId = adbExecutor($"-s {serial} shell su -c id", false, 5000);
            if (suId.Success && (suId.Output.Contains("uid=0") || suId.Output.Contains("root")))
                return true;
        }

        // Test 3: Magisk Manager instalado
        var magiskCheck = adbExecutor($"-s {serial} shell pm list packages 2>/dev/null | grep -i magisk", false, 5000);
        if (magiskCheck.Success && !string.IsNullOrWhiteSpace(magiskCheck.Output))
            return true;

        // Test 4: adb root
        var adbRoot = adbExecutor($"-s {serial} root", false, 5000);
        if (adbRoot.Success && adbRoot.Output.Contains("already running as root"))
            return true;

        return false;
    }

    private static bool ContainsMagiskSignature(byte[] data)
    {
        // Firmas conocidas de Magisk en boot.img parcheado
        var signatures = new[]
        {
            Encoding.ASCII.GetBytes("magisk"),
            Encoding.ASCII.GetBytes("Magisk"),
            Encoding.ASCII.GetBytes("sbin/magisk"),
            Encoding.ASCII.GetBytes("overlay.d"),
            Encoding.ASCII.GetBytes("magiskinit"),
            new byte[] { 0x4D, 0x61, 0x67, 0x69, 0x73, 0x6B } // "Magisk" en hex
        };

        foreach (var sig in signatures)
        {
            if (ContainsBytes(data, sig))
                return true;
        }

        return false;
    }

    private static bool ContainsBytes(byte[] source, byte[] pattern)
    {
        if (pattern.Length > source.Length) return false;

        for (int i = 0; i <= source.Length - pattern.Length; i++)
        {
            bool found = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j])
                {
                    found = false;
                    break;
                }
            }
            if (found) return true;
        }
        return false;
    }

    private static string CalculateSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private static byte[]? ReadExact(FileStream stream, int count)
    {
        var buffer = new byte[count];
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, totalRead, count - totalRead);
            if (read == 0) return totalRead > 0 ? buffer[..totalRead] : null;
            totalRead += read;
        }
        return buffer;
    }
}
