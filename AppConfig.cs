using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Peek;

internal static class RocoGame
{
    public const string DisplayName = "Roco Kingdom: World";
    public const string SearchPrefix = "洛克王国";
}

internal sealed class AppConfig
{
    public const string BilibiliSearchUrlPrefix = "https://search.bilibili.com/all?keyword=";
    public const string DefaultModel = "gemini-3.1-flash-lite";

    public string ApiKey { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = "English";

    public static bool HasGeminiApiKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().StartsWith("AIza", StringComparison.OrdinalIgnoreCase);

    public static string NormalizeTargetLanguage(string? value)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Contains("viet", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("việt", StringComparison.OrdinalIgnoreCase))
        {
            return "Vietnamese";
        }

        return "English";
    }
}

internal static class AppConfigStore
{
    private sealed class StoredAppConfig
    {
        public string EncryptedApiKey { get; set; } = string.Empty;
        public string TargetLanguage { get; set; } = "English";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string ConfigPath =>
        Path.Combine(AppPaths.DataDirectory, "settings.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return new AppConfig();
            }

            var stored = JsonSerializer.Deserialize<StoredAppConfig>(File.ReadAllText(ConfigPath)) ?? new StoredAppConfig();
            return new AppConfig
            {
                ApiKey = Unprotect(stored.EncryptedApiKey),
                TargetLanguage = AppConfig.NormalizeTargetLanguage(stored.TargetLanguage)
            };
        }
        catch (IOException)
        {
            return new AppConfig();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppConfig();
        }
        catch (SecurityException)
        {
            return new AppConfig();
        }
        catch (JsonException)
        {
            return new AppConfig();
        }
        catch (CryptographicException)
        {
            return new AppConfig();
        }
        catch (FormatException)
        {
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);

        var stored = new StoredAppConfig
        {
            EncryptedApiKey = Protect(config.ApiKey),
            TargetLanguage = AppConfig.NormalizeTargetLanguage(config.TargetLanguage)
        };

        var tempPath = ConfigPath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(stored, JsonOptions));
            File.Move(tempPath, ConfigPath, true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static string Protect(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string Unprotect(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var encrypted = Convert.FromBase64String(value);
        var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }
}
