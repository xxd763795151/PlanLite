using System.IO;
using Microsoft.Win32;

namespace PlanLite.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "PlanLite";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(AppValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public bool TrySetEnabled(bool enabled, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                errorMessage = "无法打开 Windows 启动项注册表键。";
                return false;
            }

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    errorMessage = "无法定位当前可执行文件路径。";
                    return false;
                }

                key.SetValue(AppValueName, $"\"{Path.GetFullPath(exePath)}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(AppValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"设置开机自启动失败: {ex.Message}";
            return false;
        }
    }
}
