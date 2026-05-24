using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace Peek;

internal static class SkillDatabase
{
    private static readonly Lazy<SkillIndex> Index = new(Load);

    public static SkillLookupResult Lookup(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var matched = new List<SkillEntry>();
        var unmatched = new List<string>();
        var seenSkills = new HashSet<string>(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawName in names)
        {
            var name = CleanName(rawName);
            if (string.IsNullOrWhiteSpace(name) || !seenNames.Add(name))
            {
                continue;
            }

            if (Index.Value.ByName.TryGetValue(name, out var skill) ||
                Index.Value.ByCompactName.TryGetValue(CompactName(name), out skill))
            {
                if (seenSkills.Add(skill.Id))
                {
                    matched.Add(skill);
                }

                continue;
            }

            unmatched.Add(name);
        }

        return new SkillLookupResult(matched, unmatched);
    }

    public static string GetLocalizedName(SkillEntry skill, string targetLanguage) =>
        GetLocalization(skill, targetLanguage)?.Name ?? skill.NameZh;

    public static string GetLocalizedDescription(SkillEntry skill, string targetLanguage) =>
        GetLocalization(skill, targetLanguage)?.Description ?? skill.DescriptionZh;

    public static string GetElementLabel(string element, string targetLanguage)
    {
        var labels = IsVietnamese(targetLanguage) ? ElementLabelsVi : ElementLabelsEn;
        return labels.TryGetValue(element, out var label) ? label : element;
    }

    private static SkillLocalization? GetLocalization(SkillEntry skill, string targetLanguage)
    {
        var localization = IsVietnamese(targetLanguage)
            ? skill.Translations?.Vi
            : skill.Translations?.En;

        return string.IsNullOrWhiteSpace(localization?.Name) ||
            localization.Description is null ||
            (!string.IsNullOrWhiteSpace(skill.DescriptionZh) && string.IsNullOrWhiteSpace(localization.Description)) ||
            !string.Equals(localization.TranslatedFromHash, skill.SourceHash, StringComparison.Ordinal)
                ? null
                : localization;
    }

    private static SkillIndex Load()
    {
        var streamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/Data/skills.json", UriKind.Absolute)) ??
            throw new InvalidOperationException("Bundled skill database is missing.");
        using var stream = streamInfo.Stream;
        using var reader = new StreamReader(stream);
        var data = JsonSerializer.Deserialize<SkillDataFile>(reader.ReadToEnd()) ??
            throw new InvalidOperationException("Bundled skill database is invalid.");
        if (data.SchemaVersion != 2 ||
            data.Source?.Provider != "wikiroco" ||
            data.Source.Url != "https://wikiroco.com/api/skills" ||
            data.Source.ItemCount != data.Skills.Count ||
            data.Source.SourceCount < data.Skills.Count)
        {
            throw new InvalidOperationException("Bundled skill database has an unsupported schema.");
        }

        var byName = new Dictionary<string, SkillEntry>(StringComparer.Ordinal);
        var byCompactName = new Dictionary<string, SkillEntry>(StringComparer.Ordinal);
        foreach (var skill in data.Skills)
        {
            AddName(byName, byCompactName, skill.NameZh, skill);
            foreach (var alias in skill.AliasesZh)
            {
                AddName(byName, byCompactName, alias, skill);
            }
        }

        return new SkillIndex(byName, byCompactName);
    }

    private static void AddName(
        Dictionary<string, SkillEntry> byName,
        Dictionary<string, SkillEntry> byCompactName,
        string name,
        SkillEntry skill)
    {
        name = CleanName(name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        byName.TryAdd(name, skill);
        byCompactName.TryAdd(CompactName(name), skill);
    }

    private static string CleanName(string? value) =>
        value?
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim() ?? string.Empty;

    private static string CompactName(string value) =>
        string.Concat(value.Where(static c => !char.IsWhiteSpace(c)));

    private static bool IsVietnamese(string targetLanguage) =>
        targetLanguage.Contains("viet", StringComparison.OrdinalIgnoreCase) ||
        targetLanguage.Contains("việt", StringComparison.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> ElementLabelsEn = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["water"] = "Water",
        ["fire"] = "Fire",
        ["grass"] = "Grass",
        ["electric"] = "Electric",
        ["ice"] = "Ice",
        ["fighting"] = "Fighting",
        ["poison"] = "Poison",
        ["ground"] = "Ground",
        ["flying"] = "Flying",
        ["bug"] = "Bug",
        ["ghost"] = "Ghost",
        ["dragon"] = "Dragon",
        ["dark"] = "Dark",
        ["steel"] = "Steel",
        ["fairy"] = "Fairy",
        ["normal"] = "Normal",
        ["light"] = "Light",
        ["illusion"] = "Illusion"
    };

    private static readonly IReadOnlyDictionary<string, string> ElementLabelsVi = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["water"] = "Nước",
        ["fire"] = "Lửa",
        ["grass"] = "Cỏ",
        ["electric"] = "Điện",
        ["ice"] = "Băng",
        ["fighting"] = "Võ",
        ["poison"] = "Độc",
        ["ground"] = "Đất",
        ["flying"] = "Bay",
        ["bug"] = "Côn trùng",
        ["ghost"] = "Ma",
        ["dragon"] = "Rồng",
        ["dark"] = "Bóng tối",
        ["steel"] = "Máy móc",
        ["fairy"] = "Tiên",
        ["normal"] = "Thường",
        ["light"] = "Ánh sáng",
        ["illusion"] = "Ảo ảnh"
    };

}

internal sealed record SkillLookupResult(
    IReadOnlyList<SkillEntry> Matched,
    IReadOnlyList<string> Unmatched);

internal sealed record SkillIndex(
    IReadOnlyDictionary<string, SkillEntry> ByName,
    IReadOnlyDictionary<string, SkillEntry> ByCompactName);

internal sealed class SkillDataFile
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("source")]
    public SkillDataSource? Source { get; set; }

    [JsonPropertyName("skills")]
    public List<SkillEntry> Skills { get; set; } = [];
}

internal sealed class SkillDataSource
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("source_count")]
    public int SourceCount { get; set; }

    [JsonPropertyName("item_count")]
    public int ItemCount { get; set; }
}

internal sealed class SkillEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name_zh")]
    public string NameZh { get; set; } = string.Empty;

    [JsonPropertyName("aliases_zh")]
    public List<string> AliasesZh { get; set; } = [];

    [JsonPropertyName("element")]
    public string Element { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("power")]
    public int? Power { get; set; }

    [JsonPropertyName("energy")]
    public int? Energy { get; set; }

    [JsonPropertyName("description_zh")]
    public string DescriptionZh { get; set; } = string.Empty;

    [JsonPropertyName("source_hash")]
    public string SourceHash { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public SkillIcon Icon { get; set; } = new();

    [JsonPropertyName("translations")]
    public SkillLocalizedSet? Translations { get; set; }
}

internal sealed class SkillIcon
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
}

internal sealed class SkillLocalizedSet
{
    [JsonPropertyName("en")]
    public SkillLocalization? En { get; set; }

    [JsonPropertyName("vi")]
    public SkillLocalization? Vi { get; set; }
}

internal sealed class SkillLocalization
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("translated_from_hash")]
    public string TranslatedFromHash { get; set; } = string.Empty;
}
