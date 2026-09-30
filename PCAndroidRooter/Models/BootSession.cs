namespace PCAndroidRooter.Models;

/// <summary>Parche pendiente de grabar, y la copia para poder volver atrás.</summary>
public sealed class BootSession
{
    public string Serial { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public string PatchedPath { get; set; } = string.Empty;
    public string FastbootPartition { get; set; } = string.Empty;
    public string? Fingerprint { get; set; }
    public bool TempBootVerified { get; set; }
    public bool IsInitBoot { get; set; }
    public bool PatchEvidence { get; set; }

    /// <summary>Samsung: no hay fastboot. Se graba en Download Mode.</summary>
    public bool UseDownloadMode { get; set; }
}
