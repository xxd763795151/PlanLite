using System.IO;
using System.Text;

namespace PlanLite.Services;

public sealed class CrashLogger
{
    private readonly string _logFilePath;
    private readonly object _sync = new();

    public CrashLogger(string logsDirectory)
    {
        _logFilePath = Path.Combine(logsDirectory, "error.log");
    }

    public void Log(string context, Exception ex)
    {
        try
        {
            lock (_sync)
            {
                var dir = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{context}]");
                sb.AppendLine(ex.ToString());
                sb.AppendLine(new string('-', 60));
                File.AppendAllText(_logFilePath, sb.ToString());
            }
        }
        catch
        {
            // Logging must never crash the process.
        }
    }
}
