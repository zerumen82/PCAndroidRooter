namespace PCAndroidRooter.Models;

public class BootPartitionInfo
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool Accessible { get; set; }
    public string BlockDevice { get; set; } = string.Empty;
}
