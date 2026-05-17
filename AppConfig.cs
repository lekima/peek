using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Peek;

internal enum TargetGame
{
    None,
    RocoKingdomWorld,
    HonorOfKingsWorld,
    HonorOfKings
}

internal static class TargetGames
{
    public static string GetDisplayName(TargetGame game) =>
        game switch
        {
            TargetGame.RocoKingdomWorld => "洛克王国 / Roco Kingdom: World",
            TargetGame.HonorOfKingsWorld => "王者荣耀世界 / Honor of Kings: World",
            TargetGame.HonorOfKings => "王者荣耀 / Honor of Kings",
            _ => "No specific game"
        };

    public static string GetSearchPrefix(TargetGame game) =>
        game switch
        {
            TargetGame.RocoKingdomWorld => "洛克王国",
            TargetGame.HonorOfKingsWorld => "王者荣耀世界",
            TargetGame.HonorOfKings => "王者荣耀",
            _ => string.Empty
        };
}

internal sealed class AppConfig
{
    public const string BilibiliSearchUrlTemplate = "https://search.bilibili.com/all?keyword={0}";
    public const string Gemini31FlashLiteModel = "google/gemini-3.1-flash-lite";

    public string ApiKey { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = "English";
    public TargetGame TargetGame { get; set; } = TargetGame.None;
    public decimal TotalCostUsd { get; set; }
}

internal static class AppConfigStore
{
    private sealed class StoredAppConfig
    {
        public string EncryptedApiKey { get; set; } = string.Empty;
        public string TargetLanguage { get; set; } = "English";
        public string TargetGame { get; set; } = nameof(Peek.TargetGame.None);
        public decimal TotalCostUsd { get; set; }
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
                TargetLanguage = string.IsNullOrWhiteSpace(stored.TargetLanguage) ? "English" : stored.TargetLanguage,
                TargetGame = NormalizeTargetGame(stored.TargetGame),
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
            TargetLanguage = string.IsNullOrWhiteSpace(config.TargetLanguage) ? "English" : config.TargetLanguage,
            TargetGame = config.TargetGame.ToString(),
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

    private static TargetGame NormalizeTargetGame(string value)
    {
        return Enum.TryParse<TargetGame>(value, true, out var targetGame) &&
            Enum.IsDefined(targetGame)
            ? targetGame
            : TargetGame.None;
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
