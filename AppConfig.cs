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
    public static readonly ModelOption[] ModelOptions =
    [
        new("Text", "Gemini 3.1 Flash Lite Preview", Gemini31FlashLiteModel),
        new("Image", "Gemini 3.1 Flash Image Preview", Gemini31FlashImageModel)
    ];

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = DefaultModel;
    public string FromLanguage { get; set; } = "Chinese";
    public string ToLanguage { get; set; } = "Vietnamese";
    public decimal TotalCostUsd { get; set; }

    public static bool IsImageEditModel(string model) =>
        string.Equals(model, Gemini31FlashImageModel, StringComparison.OrdinalIgnoreCase);
}

public sealed record ModelOption(string Mode, string Name, string Id);

public sealed class StoredAppConfig
{
    public string EncryptedApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = AppConfig.DefaultModel;
    public string FromLanguage { get; set; } = "Chinese";
    public string ToLanguage { get; set; } = "Vietnamese";
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
                ToLanguage = string.IsNullOrWhiteSpace(stored.ToLanguage) ? "Vietnamese" : stored.ToLanguage,
                TotalCostUsd = stored.TotalCostUsd
            };
        }
        catch
        {
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);

        var stored = new StoredAppConfig
        {
            EncryptedApiKey = Protect(config.ApiKey),
            Model = string.IsNullOrWhiteSpace(config.Model) ? AppConfig.DefaultModel : config.Model,
            FromLanguage = string.IsNullOrWhiteSpace(config.FromLanguage) ? "Chinese" : config.FromLanguage,
            ToLanguage = string.IsNullOrWhiteSpace(config.ToLanguage) ? "Vietnamese" : config.ToLanguage,
            TotalCostUsd = config.TotalCostUsd
        };

        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(stored, JsonOptions));
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

        return AppConfig.ModelOptions.Any(option => string.Equals(option.Id, model, StringComparison.OrdinalIgnoreCase))
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
