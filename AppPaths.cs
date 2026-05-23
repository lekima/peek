using System.Diagnostics;
using System.IO;

namespace Peek;

internal static class AppPaths
{
    public static string AppDirectory
    {
        get
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                processPath = Process.GetCurrentProcess().MainModule?.FileName;
            }

            var directory = string.IsNullOrWhiteSpace(processPath)
                ? AppContext.BaseDirectory
                : Path.GetDirectoryName(processPath);

            return string.IsNullOrWhiteSpace(directory)
                ? Directory.GetCurrentDirectory()
                : directory;
        }
    }

    public static string DataDirectory
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(localAppData)
                ? AppDirectory
                : Path.Combine(localAppData, "Peek");
        }
    }
}
