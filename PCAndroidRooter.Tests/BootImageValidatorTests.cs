using System.IO;
using PCAndroidRooter.Services;
using Xunit;

namespace PCAndroidRooter.Tests;

public class BootImageValidatorTests : IDisposable
{
    private readonly string _dir;

    public BootImageValidatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pcar_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] MakeBootImage(int size)
    {
        var data = new byte[size];
        var magic = System.Text.Encoding.ASCII.GetBytes("ANDROID!");
        Array.Copy(magic, data, magic.Length);
        return data;
    }

    [Fact]
    public void ValidateOriginal_MissingFile_ReturnsUnknownError()
    {
        var result = BootImageValidator.ValidateOriginal(Path.Combine(_dir, "nope.img"));
        Assert.Equal(BootImageValidator.ValidationStatus.UnknownError, result.Status);
    }

    [Fact]
    public void ValidateOriginal_TooSmall_ReturnsTooSmall()
    {
        var path = WriteFile("small.img", MakeBootImage(100));
        var result = BootImageValidator.ValidateOriginal(path);
        Assert.Equal(BootImageValidator.ValidationStatus.TooSmall, result.Status);
    }

    [Fact]
    public void ValidateOriginal_ValidMagicAndSize_ReturnsValid()
    {
        var path = WriteFile("ok.img", MakeBootImage(BootImageValidator.BootImageMinSize + 4096));
        var result = BootImageValidator.ValidateOriginal(path);
        Assert.Equal(BootImageValidator.ValidationStatus.Valid, result.Status);
        Assert.False(string.IsNullOrEmpty(result.Sha256));
    }

    [Fact]
    public void ValidateOriginal_BadMagic_ReturnsInvalidMagic()
    {
        var data = MakeBootImage(BootImageValidator.BootImageMinSize + 1024);
        data[0] = (byte)'X';
        var path = WriteFile("bad_magic.img", data);
        var result = BootImageValidator.ValidateOriginal(path);
        Assert.Equal(BootImageValidator.ValidationStatus.InvalidMagic, result.Status);
    }

    [Fact]
    public void ValidatePatched_MissingOriginal_ReturnsError()
    {
        var patched = WriteFile("p.img", MakeBootImage(BootImageValidator.BootImageMinSize + 1024));
        var result = BootImageValidator.ValidatePatched(patched, Path.Combine(_dir, "missing.img"));
        Assert.NotEqual(BootImageValidator.ValidationStatus.Valid, result.Status);
    }
}
