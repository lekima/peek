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
    HonorOfKingsChess,
    HonorOfKings
}

internal static class TargetGames
{
    public static IReadOnlyList<TargetGame> MenuGames { get; } =
    [
        TargetGame.None,
        TargetGame.RocoKingdomWorld,
        TargetGame.HonorOfKingsWorld,
        TargetGame.HonorOfKingsChess,
        TargetGame.HonorOfKings
    ];

    public static string GetDisplayName(TargetGame game) =>
        game switch
        {
            TargetGame.RocoKingdomWorld => "Roco Kingdom: World",
            TargetGame.HonorOfKingsWorld => "Honor of Kings: World",
            TargetGame.HonorOfKingsChess => "Honor of Kings: Chess",
            TargetGame.HonorOfKings => "Honor of Kings",
            _ => "Any game"
        };

    public static string GetSearchPrefix(TargetGame game) =>
        game switch
        {
            TargetGame.RocoKingdomWorld => "洛克王国",
            TargetGame.HonorOfKingsWorld => "王者荣耀世界",
            TargetGame.HonorOfKingsChess => "王者万象棋",
            TargetGame.HonorOfKings => "王者荣耀",
            _ => string.Empty
        };
}

internal enum AppMode
{
    Game,
    Chat
}

internal static class AppModes
{
    public static IReadOnlyList<AppMode> MenuModes { get; } =
    [
        AppMode.Game,
        AppMode.Chat
    ];

    public static string GetDisplayName(AppMode mode) =>
        mode switch
        {
            AppMode.Chat => "Chat mode",
            _ => "Game mode"
        };
}

internal sealed class AppConfig
{
    public const string BilibiliSearchUrlPrefix = "https://search.bilibili.com/all?keyword=";
    public const string DefaultModel = "gemini-3.1-flash-lite";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = DefaultModel;
    public string TargetLanguage { get; set; } = "English";
    public TargetGame TargetGame { get; set; } = TargetGame.None;
    public AppMode Mode { get; set; } = AppMode.Game;

    public static bool HasGeminiApiKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().StartsWith("AIza", StringComparison.OrdinalIgnoreCase);

    public static string NormalizeModel(string? value)
    {
        value = value?.Trim() ?? string.Empty;
        return value.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
            ? value.Trim()
            : DefaultModel;
    }

    public static string NormalizeTargetLanguage(string value) =>
        string.IsNullOrWhiteSpace(value) ? "English" : value.Trim();
}

internal static class AppConfigStore
{
    private sealed class StoredAppConfig
    {
        public string EncryptedApiKey { get; set; } = string.Empty;
        public string Model { get; set; } = AppConfig.DefaultModel;
        public string TargetLanguage { get; set; } = "English";
        public string TargetGame { get; set; } = nameof(Peek.TargetGame.None);
        public string Mode { get; set; } = nameof(AppMode.Game);
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
                Model = AppConfig.NormalizeModel(stored.Model),
                TargetLanguage = AppConfig.NormalizeTargetLanguage(stored.TargetLanguage),
                TargetGame = NormalizeTargetGame(stored.TargetGame),
                Mode = NormalizeMode(stored.Mode)
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
            Model = AppConfig.NormalizeModel(config.Model),
            TargetLanguage = AppConfig.NormalizeTargetLanguage(config.TargetLanguage),
            TargetGame = config.TargetGame.ToString(),
            Mode = config.Mode.ToString()
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

    private static AppMode NormalizeMode(string value)
    {
        return Enum.TryParse<AppMode>(value, true, out var mode) &&
            Enum.IsDefined(mode)
            ? mode
            : AppMode.Game;
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
