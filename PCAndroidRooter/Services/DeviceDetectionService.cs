using System.Timers;
using PCAndroidRooter.Models;
using Timer = System.Timers.Timer;

namespace PCAndroidRooter.Services;

public class DeviceDetectionService : IDisposable
{
    private readonly AdbService _adbService;
    private Timer? _pollTimer;
    private HashSet<string> _lastDevices = new();
    private bool _disposed;

    public event Action<List<string>>? DevicesUpdated;
    public event Action<string>? DeviceConnected;
    public event Action<string>? DeviceDisconnected;

    public DeviceDetectionService(AdbService adbService)
    {
        _adbService = adbService;
    }

    public void Start(int intervalMs = 2000)
    {
        _pollTimer = new Timer(intervalMs);
        _pollTimer.Elapsed += OnPollTimerElapsed;
        _pollTimer.AutoReset = true;
        _pollTimer.Start();
    }

    public void Stop()
    {
        _pollTimer?.Stop();
    }

    private void OnPollTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        try
        {
            var currentDevices = new HashSet<string>(_adbService.GetConnectedDevices());

            var connected = currentDevices.Except(_lastDevices).ToList();
            var disconnected = _lastDevices.Except(currentDevices).ToList();

            foreach (var device in connected)
                DeviceConnected?.Invoke(device);

            foreach (var device in disconnected)
                DeviceDisconnected?.Invoke(device);

            if (connected.Count > 0 || disconnected.Count > 0 || !_lastDevices.SetEquals(currentDevices))
            {
                _lastDevices = currentDevices;
                DevicesUpdated?.Invoke(currentDevices.ToList());
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer?.Stop();
        _pollTimer?.Dispose();
    }
}
