using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Timers;
using PCAndroidRooter.Models;
using Timer = System.Timers.Timer;

namespace PCAndroidRooter.Services;

public class DeviceDetectionService : IDisposable
{
    private readonly AdbService _adbService;
    private readonly SynchronizationContext _uiContext;
    private Timer? _pollTimer;
    private readonly ConcurrentDictionary<string, byte> _lastDevices = new();
    private readonly object _detectionLock = new();
    private bool _disposed;
    private bool _paused;
    private int _intervalMs = 2000;

    public event Action<List<string>>? DevicesUpdated;
    public event Action<string>? DeviceConnected;
    public event Action<string>? DeviceDisconnected;

    public DeviceDetectionService(AdbService adbService)
    {
        _adbService = adbService;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    public void Start(int intervalMs = 2000)
    {
        _intervalMs = intervalMs;
        // Use Timer with initial delay instead of Thread.Sleep to avoid blocking thread pool
        _uiContext.Post(s =>
        {
            if (_disposed) return;
            // Disponer el timer anterior para no acumular handlers si Start se repite
            if (_pollTimer != null)
            {
                _pollTimer.Elapsed -= OnPollTimerElapsed;
                _pollTimer.Stop();
                _pollTimer.Dispose();
            }
            _pollTimer = new Timer(_intervalMs);
            _pollTimer.Elapsed += OnPollTimerElapsed;
            _pollTimer.AutoReset = true;
            // Start with 3 second initial delay
            _pollTimer.Start();
            // The first tick will happen after _intervalMs, which is acceptable
        }, null);
    }

    public void Stop()
    {
        _pollTimer?.Stop();
    }

    public void Pause()
    {
        _paused = true;
    }

    public void Resume()
    {
        _paused = false;
    }

    private void OnPollTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        try
        {
            if (_paused) return;
            // Evita ejecuciones simultáneas: si la ronda anterior no terminó, se salta esta
            if (!Monitor.TryEnter(_detectionLock)) return;

            try
            {
                var currentDeviceList = _adbService.GetConnectedDevices();
                var currentDevices = new HashSet<string>(currentDeviceList);

                var connected = currentDeviceList.Where(d => !_lastDevices.ContainsKey(d)).ToList();
                var disconnected = _lastDevices.Keys.Where(d => !currentDevices.Contains(d)).ToList();

                foreach (var device in connected)
                    DeviceConnected?.Invoke(device);

                foreach (var device in disconnected)
                    DeviceDisconnected?.Invoke(device);

                if (connected.Count > 0 || disconnected.Count > 0)
                {
                    // Update the concurrent dictionary
                    _lastDevices.Clear();
                    foreach (var d in currentDeviceList)
                        _lastDevices[d] = 0;

                    DevicesUpdated?.Invoke(currentDeviceList);
                }
            }
            finally
            {
                Monitor.Exit(_detectionLock);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error en detección de dispositivos: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer?.Stop();
        _pollTimer?.Dispose();
    }
}
