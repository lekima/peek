using System.IO;
using System.Security;

namespace Peek;

internal static class AppDataMaintenance
{
    public static void ClearSensitiveData()
    {
        AppLogger.ClearLogs();
        DeleteCaptures();
    }

    public static void DeleteCaptures()
    {
        var capturesDirectory = Path.Combine(AppPaths.DataDirectory, "captures");
        try
        {
            if (Directory.Exists(capturesDirectory))
            {
                Directory.Delete(capturesDirectory, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            AppLogger.Error("Could not clear saved captures.", ex);
        }
    }
}
