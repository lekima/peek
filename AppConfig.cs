using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Peek;

public sealed class AppConfig
{
    public const string Gemini31FlashLiteModel = "google/gemini-3.1-flash-lite-preview";
    public const string Gemini31FlashImageModel = "google/gemini-3.1-flash-image-preview";
    public const string DefaultModel = Gemini31FlashLiteModel;

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = DefaultModel;
    public string FromLanguage { get; set; } = "Chinese";
    public string ToLanguage { get; set; } = "English";
    public decimal TotalCostUsd { get; set; }

    public static bool IsImageEditModel(string model) =>
        string.Equals(model, Gemini31FlashImageModel, StringComparison.OrdinalIgnoreCase);
}

public sealed class StoredAppConfig
{
    public string EncryptedApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = AppConfig.DefaultModel;
    public string FromLanguage { get; set; } = "Chinese";
    public string ToLanguage { get; set; } = "English";
    public decimal TotalCostUsd { get; set; }
}

public static class AppConfigStore
{
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
                Model = NormalizeModel(stored.Model),
                FromLanguage = string.IsNullOrWhiteSpace(stored.FromLanguage) ? "Chinese" : stored.FromLanguage,
                ToLanguage = string.IsNullOrWhiteSpace(stored.ToLanguage) ? "English" : stored.ToLanguage,
                TotalCostUsd = stored.TotalCostUsd
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
            Model = string.IsNullOrWhiteSpace(config.Model) ? AppConfig.DefaultModel : config.Model,
            FromLanguage = string.IsNullOrWhiteSpace(config.FromLanguage) ? "Chinese" : config.FromLanguage,
            ToLanguage = string.IsNullOrWhiteSpace(config.ToLanguage) ? "English" : config.ToLanguage,
            TotalCostUsd = config.TotalCostUsd
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

    private static string NormalizeModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return AppConfig.DefaultModel;
        }

        return string.Equals(model, AppConfig.Gemini31FlashLiteModel, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(model, AppConfig.Gemini31FlashImageModel, StringComparison.OrdinalIgnoreCase)
            ? model
            : AppConfig.DefaultModel;
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
