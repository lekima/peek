using System.Diagnostics;
using Microsoft.Win32;

namespace Peek;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Peek";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(AppName) is string value &&
               string.Equals(value, GetStartupCommand(), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true) ??
                        Registry.CurrentUser.CreateSubKey(RunKeyPath, true);

        if (enabled)
        {
            key.SetValue(AppName, GetStartupCommand(), RegistryValueKind.String);
            AppLogger.Info("Windows startup enabled.");
            return;
        }

        key.DeleteValue(AppName, false);
        AppLogger.Info("Windows startup disabled.");
    }

    private static string GetStartupCommand()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Could not resolve Peek executable path.");
        }

        return $"\"{path}\"";
    }
}
