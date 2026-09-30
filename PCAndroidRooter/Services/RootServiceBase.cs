using System;
using System.Text;
using PCAndroidRooter.Models;

namespace PCAndroidRooter.Services;

/// <summary>
/// Base compartida por los servicios del flujo de boot: buffer de log,
/// eventos y helpers de logging. Sin lógica de negocio.
/// Cero cambios de comportamiento respecto al RootService monolítico:
/// mismos formatos de log y mismos eventos.
/// </summary>
public abstract class RootServiceBase
{
    private readonly StringBuilder _logOutput = new();
    private readonly object _logLock = new();

    public event Action<string>? LogUpdated;
    public event Action<RootMethodType, RootMethodStatus>? MethodStatusChanged;

    public string LogOutput
    {
        get { lock (_logLock) { return _logOutput.ToString(); } }
    }

    protected void RaiseMethodStatus(RootMethodType type, RootMethodStatus status) =>
        MethodStatusChanged?.Invoke(type, status);

    protected void Log(string message)
    {
        lock (_logLock)
        {
            _logOutput.AppendLine(message);
        }
        LogUpdated?.Invoke(message);
    }

    protected void LogOk(string message) => Log($"✅ [OK] {message}");
    protected void LogError(string message) => Log($"❌ [ERROR] {message}");
    protected void LogWarning(string message) => Log($"⚠ [AVISO] {message}");
}
