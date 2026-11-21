using System.IO;
using System.Text;

namespace PlanLite.Services;

public sealed class CrashLogger
{
    public const long DefaultMaxFileBytes = 1024 * 1024;
    public static readonly TimeSpan DefaultDuplicateCooldown = TimeSpan.FromSeconds(10);

    private readonly string _logFilePath;
    private readonly string _backupLogFilePath;
    private readonly long _maxFileBytes;
    private readonly TimeSpan _duplicateCooldown;
    private readonly object _sync = new();

    private string? _lastSignature;
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private int _suppressedCount;

    public CrashLogger(string logsDirectory)
        : this(logsDirectory, DefaultMaxFileBytes, DefaultDuplicateCooldown)
    {
    }

    internal CrashLogger(string logsDirectory, long maxFileBytes, TimeSpan duplicateCooldown)
    {
        _logFilePath = Path.Combine(logsDirectory, "error.log");
        _backupLogFilePath = Path.Combine(logsDirectory, "error.1.log");
        _maxFileBytes = Math.Max(1024, maxFileBytes);
        _duplicateCooldown = duplicateCooldown < TimeSpan.Zero ? TimeSpan.Zero : duplicateCooldown;
    }

    public void Log(string context, Exception ex)
    {
        try
        {
            var nowUtc = DateTime.UtcNow;
            var signature = $"{context}|{ex.GetType().FullName}|{ex.Message}";

            lock (_sync)
            {
                if (_lastSignature is not null
                    && string.Equals(_lastSignature, signature, StringComparison.Ordinal)
                    && nowUtc - _lastWriteUtc < _duplicateCooldown)
                {
                    _suppressedCount++;
                    return;
                }

                var dir = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                RotateIfNeeded();

                var sb = new StringBuilder();
                if (_suppressedCount > 0)
                {
                    sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [Suppressed] 上述相同异常重复发生 {_suppressedCount} 次（已折叠）");
                    sb.AppendLine(new string('-', 60));
                }

                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{context}]");
                sb.AppendLine(ex.ToString());
                sb.AppendLine(new string('-', 60));
                File.AppendAllText(_logFilePath, sb.ToString());

                _suppressedCount = 0;
                _lastSignature = signature;
                _lastWriteUtc = nowUtc;
            }
        }
        catch
        {
            // Logging must never crash the process.
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_logFilePath);
        if (info.Exists && info.Length >= _maxFileBytes)
        {
            File.Move(_logFilePath, _backupLogFilePath, overwrite: true);
        }
    }
}
