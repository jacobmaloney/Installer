namespace Installer.Core.Services.Conduit;

/// <summary>
/// File-backed log for the silent install (there is no UI). Optionally mirrors
/// to an extra sink (attached parent console). Never throws from a log call.
/// </summary>
public sealed class SilentInstallLog : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly Action<string>? _mirror;
    private readonly object _gate = new();

    public string? LogFilePath { get; }

    public SilentInstallLog(string logFilePath, Action<string>? mirror = null)
    {
        _mirror = mirror;
        try
        {
            var dir = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            _writer = new StreamWriter(logFilePath, append: true) { AutoFlush = true };
            LogFilePath = logFilePath;
        }
        catch
        {
            _writer = null;
            LogFilePath = null;
        }
    }

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (_gate)
        {
            try { _writer?.WriteLine(line); } catch { }
            try { _mirror?.Invoke(line); } catch { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _writer?.Dispose(); } catch { }
        }
    }
}
