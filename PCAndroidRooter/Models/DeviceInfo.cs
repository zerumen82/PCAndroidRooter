namespace PCAndroidRooter.Models;

public class DeviceInfo
{
    public string Serial { get; set; } = string.Empty;
    public string Model { get; set; } = "Desconocido";
    public string Manufacturer { get; set; } = "Desconocido";
    public string AndroidVersion { get; set; } = "Desconocido";
    public string BuildNumber { get; set; } = "Desconocido";
    public string ProductName { get; set; } = "Desconocido";
    public bool IsRecovery { get; set; }
    public bool IsFastboot { get; set; }
    public bool IsAuthorized { get; set; }
    public bool BootloaderUnlocked { get; set; }
    public bool IsRooted { get; set; }
    public string BatteryLevel { get; set; } = "Desconocido";
    public string Abi { get; set; } = "Desconocido";
    public string SecurityPatch { get; set; } = "Desconocido";
    public string ConnectionStatus { get; set; } = "Desconectado";
    public string CpuInfo { get; set; } = "Desconocido";
    public long TotalRam { get; set; }
    public long TotalStorage { get; set; }
}
