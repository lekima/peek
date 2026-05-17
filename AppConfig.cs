using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Peek;

internal enum ResultFormat
{
    Text,
    Image
}

internal enum GameSearchPrefix
{
    None,
    HonorOfKingsWorld,
    RocoKingdomWorld
}

internal enum SearchSource
{
    Bilibili,
    YouTube,
    YouTubeChinese
}

internal sealed record SearchProfile(
    SearchSource Source,
    string DisplayName,
    string PromptName,
    string SearchLanguage,
    string UrlTemplate);

internal static class SearchProfiles
{
    public static readonly SearchProfile Bilibili = new(
        SearchSource.Bilibili,
        "Bilibili - Chinese",
        "Bilibili",
        "Chinese",
        "https://search.bilibili.com/all?keyword={0}");

    public static readonly SearchProfile YouTube = new(
        SearchSource.YouTube,
        "YouTube - English",
        "YouTube",
        "English",
        "https://www.youtube.com/results?search_query={0}");

    public static readonly SearchProfile YouTubeChinese = new(
        SearchSource.YouTubeChinese,
        "YouTube - Chinese",
        "YouTube",
        "Chinese",
        "https://www.youtube.com/results?search_query={0}");

    public static SearchProfile Get(SearchSource source) =>
        source switch
        {
            SearchSource.YouTubeChinese => YouTubeChinese,
            SearchSource.YouTube => YouTube,
            _ => Bilibili
        };
}

internal static class GameSearchPrefixes
{
    public static string GetDisplayName(GameSearchPrefix prefix) =>
        prefix switch
        {
            GameSearchPrefix.HonorOfKingsWorld => "王者荣耀世界 / Honor of Kings: World",
            GameSearchPrefix.RocoKingdomWorld => "洛克王国 / Roco Kingdom: World",
            _ => "No prefix"
        };

    public static string GetSearchPrefix(GameSearchPrefix prefix, SearchSource source) =>
        (prefix, source) switch
        {
            (GameSearchPrefix.HonorOfKingsWorld, SearchSource.YouTube) => "Honor of Kings: World",
            (GameSearchPrefix.HonorOfKingsWorld, _) => "王者荣耀世界",
            (GameSearchPrefix.RocoKingdomWorld, SearchSource.YouTube) => "Roco Kingdom: World",
            (GameSearchPrefix.RocoKingdomWorld, _) => "洛克王国",
            _ => string.Empty
        };
}

internal sealed class AppConfig
{
    public const string Gemini31FlashLiteModel = "google/gemini-3.1-flash-lite";
    public const string Gemini31FlashImageModel = "google/gemini-3.1-flash-image-preview";

    public string ApiKey { get; set; } = string.Empty;
    public ResultFormat ResultFormat { get; set; } = ResultFormat.Text;
    public string FromLanguage { get; set; } = "Chinese";
    public string ToLanguage { get; set; } = "English";
    public SearchSource SearchSource { get; set; } = SearchSource.Bilibili;
    public GameSearchPrefix GameSearchPrefix { get; set; } = GameSearchPrefix.None;
    public decimal TotalCostUsd { get; set; }

    public static string GetModel(ResultFormat resultFormat) =>
        resultFormat == ResultFormat.Image ? Gemini31FlashImageModel : Gemini31FlashLiteModel;
}

internal static class AppConfigStore
{
    private sealed class StoredAppConfig
    {
        public string EncryptedApiKey { get; set; } = string.Empty;
        public string ResultFormat { get; set; } = nameof(Peek.ResultFormat.Text);
        public string FromLanguage { get; set; } = "Chinese";
        public string ToLanguage { get; set; } = "English";
        public string SearchSource { get; set; } = nameof(Peek.SearchSource.Bilibili);
        public string GameSearchPrefix { get; set; } = nameof(Peek.GameSearchPrefix.None);
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
                ResultFormat = NormalizeResultFormat(stored.ResultFormat),
                FromLanguage = string.IsNullOrWhiteSpace(stored.FromLanguage) ? "Chinese" : stored.FromLanguage,
                ToLanguage = string.IsNullOrWhiteSpace(stored.ToLanguage) ? "English" : stored.ToLanguage,
                SearchSource = NormalizeSearchSource(stored.SearchSource),
                GameSearchPrefix = NormalizeGameSearchPrefix(stored.GameSearchPrefix),
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
            ResultFormat = config.ResultFormat.ToString(),
            FromLanguage = string.IsNullOrWhiteSpace(config.FromLanguage) ? "Chinese" : config.FromLanguage,
            ToLanguage = string.IsNullOrWhiteSpace(config.ToLanguage) ? "English" : config.ToLanguage,
            SearchSource = config.SearchSource.ToString(),
            GameSearchPrefix = config.GameSearchPrefix.ToString(),
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

    private static ResultFormat NormalizeResultFormat(string value)
    {
        return Enum.TryParse<ResultFormat>(value, true, out var resultFormat) &&
            Enum.IsDefined(resultFormat)
            ? resultFormat
            : ResultFormat.Text;
    }

    private static GameSearchPrefix NormalizeGameSearchPrefix(string value)
    {
        return Enum.TryParse<GameSearchPrefix>(value, true, out var gameSearchPrefix) &&
            Enum.IsDefined(gameSearchPrefix)
            ? gameSearchPrefix
            : GameSearchPrefix.None;
    }

    private static SearchSource NormalizeSearchSource(string value)
    {
        return Enum.TryParse<SearchSource>(value, true, out var searchSource) &&
            Enum.IsDefined(searchSource)
            ? searchSource
            : SearchSource.Bilibili;
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
