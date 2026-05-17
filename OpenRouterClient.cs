using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Peek;

internal static class OpenRouterClient
{
    private const string TextPromptVersion = "chinese-game-bilibili-search-v7";
    private const string TextSchemaVersion = "text-result-schema-v4";
    private const string ImageEditPromptVersion = "image-edit-v2";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);
    private static readonly HttpClient HttpClient = new()
    {
        BaseAddress = new Uri("https://openrouter.ai/")
    };
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<TextTranslationResult> TranslateImageToTextAsync(
        Bitmap bitmap,
        AppConfig config,
        string model,
        string operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new InvalidOperationException("OpenRouter API key is missing.");
        }

        var targetLanguage = string.IsNullOrWhiteSpace(config.TargetLanguage) ? "English" : config.TargetLanguage.Trim();
        var targetGame = TargetGames.GetDisplayName(config.TargetGame);
        var searchPrefix = TargetGames.GetSearchPrefix(config.TargetGame);
        var imageDataUrl = ToPngDataUrl(bitmap);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["provider"] = new Dictionary<string, object?>
            {
                ["require_parameters"] = true
            },
            ["response_format"] = CreateTextTranslationResponseFormat(),
            ["messages"] = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "text",
                            text = CreateTextTranslationPrompt(targetLanguage, targetGame, searchPrefix)
                        },
                        new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = imageDataUrl
                            }
                        }
                    }
                }
            }
        };

        AppLogger.Event("ai_request", new
        {
            operationId,
            mode = "text",
            model,
            targetLanguage,
            targetGame,
            searchProfile = AppConfig.SearchProfile,
            searchSource = AppConfig.SearchSource,
            searchLanguage = AppConfig.SearchLanguage,
            searchPrefix,
            captureWidth = bitmap.Width,
            captureHeight = bitmap.Height,
            structuredOutput = true,
            requireParameters = true,
            promptVersion = TextPromptVersion,
            schemaVersion = TextSchemaVersion
        });

        var (statusCode, body) = await SendCompletionAsync(config, payload, cancellationToken).ConfigureAwait(false);
        var root = ParseSuccessfulResponse(statusCode, body, operationId);
        var message = ExtractFirstChoiceMessage(root);
        var content = ExtractTextContent(message);
        var translation = ParseTextTranslation(content, operationId);
        var providerRequestId = ExtractString(root, "id");
        var cost = ExtractCost(root);
        var usage = ExtractTokenUsage(root);
        LogUsage(operationId, providerRequestId, cost, usage);

        if (string.IsNullOrWhiteSpace(translation.Text))
        {
            throw new InvalidOperationException("No translation returned. See log.");
        }

        return new TextTranslationResult(
            translation.Text,
            translation.SearchBasis,
            translation.SearchQueries,
            cost,
            usage,
            providerRequestId);
    }

    public static async Task<ImageTranslationResult> TranslateImageToEditedImageAsync(
        Bitmap bitmap,
        AppConfig config,
        string model,
        string operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new InvalidOperationException("OpenRouter API key is missing.");
        }

        var targetLanguage = string.IsNullOrWhiteSpace(config.TargetLanguage) ? "English" : config.TargetLanguage.Trim();
        var targetGame = TargetGames.GetDisplayName(config.TargetGame);
        var searchPrefix = TargetGames.GetSearchPrefix(config.TargetGame);
        var imageDataUrl = ToPngDataUrl(bitmap);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["modalities"] = new[] { "image", "text" },
            ["messages"] = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "text",
                            text = CreateImageEditPrompt(targetLanguage, targetGame, searchPrefix)
                        },
                        new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = imageDataUrl
                            }
                        }
                    }
                }
            }
        };

        AppLogger.Event("ai_request", new
        {
            operationId,
            mode = "image_edit",
            model,
            targetLanguage,
            targetGame,
            searchPrefix,
            captureWidth = bitmap.Width,
            captureHeight = bitmap.Height,
            structuredOutput = false,
            promptVersion = ImageEditPromptVersion
        });

        var (statusCode, body) = await SendCompletionAsync(config, payload, cancellationToken).ConfigureAwait(false);
        var root = ParseSuccessfulResponse(statusCode, body, operationId);
        var message = ExtractFirstChoiceMessage(root);
        var editedImageDataUrl = ExtractFirstImageDataUrl(message);
        var providerRequestId = ExtractString(root, "id");
        var cost = ExtractCost(root);
        var usage = ExtractTokenUsage(root);
        LogUsage(operationId, providerRequestId, cost, usage);

        if (string.IsNullOrWhiteSpace(editedImageDataUrl))
        {
            throw new InvalidOperationException("No edited image returned. See log.");
        }

        return new ImageTranslationResult(editedImageDataUrl, cost, usage, providerRequestId);
    }

    private static JsonElement ParseSuccessfulResponse(HttpStatusCode statusCode, string body, string operationId)
    {
        var providerError = (int)statusCode >= 200 && (int)statusCode < 300
            ? null
            : ExtractProviderError(body);

        AppLogger.Info(
            providerError is null
                ? $"operation={operationId} openrouter.response status={(int)statusCode}"
                : $"operation={operationId} openrouter.response status={(int)statusCode} provider_error={providerError}");
        AppLogger.Event("ai_response", new
        {
            operationId,
            statusCode = (int)statusCode,
            providerError
        });

        if ((int)statusCode < 200 || (int)statusCode >= 300)
        {
            throw new InvalidOperationException(
                providerError is null
                    ? $"OpenRouter error {(int)statusCode}."
                    : $"OpenRouter error {(int)statusCode}: {providerError}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static void LogUsage(string operationId, string? providerRequestId, decimal cost, TokenUsage usage)
    {
        AppLogger.Info(
            $"operation={operationId} openrouter.usage " +
            $"provider_request_id={providerRequestId ?? "-"} " +
            $"cost={FormatCost(cost)} " +
            $"prompt_tokens={usage.PromptTokens} " +
            $"completion_tokens={usage.CompletionTokens} " +
            $"total_tokens={usage.TotalTokens}");
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> SendCompletionAsync(
        AppConfig config,
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        request.Headers.TryAddWithoutValidation("X-Title", "Peek");
        request.Content = JsonContent.Create(payload);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        try
        {
            using var response = await HttpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"OpenRouter request timed out after {RequestTimeout.TotalSeconds:0} seconds.");
        }
    }

    private static string ToPngDataUrl(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
    }

    private static string ExtractTextContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var contentElement))
        {
            return string.Empty;
        }

        if (contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString() ?? string.Empty;
        }

        if (contentElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var item in contentElement.EnumerateArray())
        {
            var text = ExtractTextPart(item);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(text);
        }

        return builder.ToString();
    }

    private static string CreateTextTranslationPrompt(
        string targetLanguage,
        string targetGame,
        string searchPrefix)
    {
        var targetGameInstruction = string.IsNullOrWhiteSpace(searchPrefix)
            ? "No specific target game is selected; infer the game only when the screenshot makes it clear."
            : $"The selected target game is {targetGame}. The app will prefix every Bilibili search with \"{searchPrefix}\"; do not include that game title in any query.";

        return
            "You are helping a player understand a Chinese game screen capture. " +
            $"Translate visible Chinese game text into natural {targetLanguage}. Preserve important player-facing details: names, numbers, counts, symbols, punctuation, and useful line breaks. " +
            "For game-specific names, prefer official localized names when clear; otherwise keep the Chinese name or transliterate rather than inventing a literal name. " +
            $"Leave text already in {targetLanguage} unchanged, and do not guess unreadable text. " +
            "Also create Bilibili guide-search queries in Chinese for the same selected content. Think like a player looking for a useful video guide after seeing this UI. " +
            "Good queries are concise and anchored to distinctive visible clues or direct inferences: quest names, items, NPCs, locations, bosses, objectives, mechanics, or unusual dialogue. " +
            "Prefer fewer strong queries over filling all 3 slots. Additional queries should offer nearby useful angles while staying close to the same intent. " +
            $"{targetGameInstruction} " +
            "For each query, include a short basis describing the clue or inference behind it. " +
            "Return only valid JSON matching the schema.";
    }

    private static string CreateImageEditPrompt(string targetLanguage, string targetGame, string searchPrefix)
    {
        var targetGameInstruction = string.IsNullOrWhiteSpace(searchPrefix)
            ? "No specific target game is selected; infer the game only when the screenshot makes it clear."
            : $"The selected target game is {targetGame}. Use that only as context for game-specific terminology.";

        return
            "You are editing a Chinese game screenshot for quick reading while preserving the original UI. " +
            $"{targetGameInstruction} " +
            $"Replace visible Chinese text with natural {targetLanguage}. " +
            "Preserve layout, colors, style, names, numbers, counts, symbols, punctuation, line breaks, and all non-text content. " +
            "For game-specific names, prefer official localized names when clear; otherwise keep the Chinese name or transliterate rather than inventing a literal name. " +
            $"Leave text already in {targetLanguage} unchanged. Keep translated text compact enough to fit the original UI. " +
            "Do not add commentary, captions, highlights, or new UI. Do not guess unreadable text; leave unreadable text unchanged. Return only the edited image.";
    }

    private static Dictionary<string, object?> CreateTextTranslationResponseFormat() =>
        new()
        {
            ["type"] = "json_schema",
            ["json_schema"] = new Dictionary<string, object?>
            {
                ["name"] = "peek_text_translation",
                ["strict"] = true,
                ["schema"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new[] { "translation", "search_basis", "search_queries" },
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["translation"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] = "Natural translated text to display to the user."
                        },
                        ["search_basis"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] = "The exact source clues, names, items, NPCs, locations, objectives, or distinctive phrases used to choose the search queries."
                        },
                        ["search_queries"] = new Dictionary<string, object?>
                        {
                            ["type"] = "array",
                            ["description"] = "Zero to three distinct Chinese gaming-guide search queries for Bilibili, ordered by usefulness.",
                            ["minItems"] = 0,
                            ["maxItems"] = 3,
                            ["items"] = new Dictionary<string, object?>
                            {
                                ["type"] = "object",
                                ["additionalProperties"] = false,
                                ["required"] = new[] { "label", "basis", "query" },
                                ["properties"] = new Dictionary<string, object?>
                                {
                                    ["label"] = new Dictionary<string, object?>
                                    {
                                        ["type"] = "string",
                                        ["enum"] = new[] { "closest", "alternative", "another_angle" },
                                        ["description"] = "The role of this query in the ordered search set."
                                    },
                                    ["basis"] = new Dictionary<string, object?>
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Visible text clue or direct inference that keeps this query close to the selected content."
                                    },
                                    ["query"] = new Dictionary<string, object?>
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Concise Chinese gaming-guide search keywords for Bilibili. Use 2 to 6 keywords. Keep close to the selected text. Do not include the game title prefix."
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

    private static ParsedTextTranslation ParseTextTranslation(string content, string operationId)
    {
        var json = ExtractJsonObject(content);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Translation response was not valid JSON.");
        }

        try
        {
            var response = JsonSerializer.Deserialize<TextTranslationResponse>(json, ResponseJsonOptions);
            var translation = response?.Translation?.Trim() ?? string.Empty;
            var searchBasis = SanitizeSearchBasis(response?.SearchBasis);
            var searchQueries = SanitizeSearchQueries(response?.SearchQueries);

            if (string.IsNullOrWhiteSpace(translation))
            {
                throw new InvalidOperationException("Translation response did not include translated text.");
            }

            return new ParsedTextTranslation(translation, searchBasis, searchQueries);
        }
        catch (JsonException ex)
        {
            AppLogger.Info($"operation={operationId} translation_json_parse_failed message={ex.Message}");
            AppLogger.Event("text_result_parse_failed", new
            {
                operationId,
                error = ex.Message,
                contentPreview = TrimForDisplay(content)
            });
            throw new InvalidOperationException("Translation response was not valid JSON.", ex);
        }
    }

    private static string ExtractJsonObject(string content)
    {
        content = content.Trim();
        if (content.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = content.IndexOf('\n', StringComparison.Ordinal);
            var lastFence = content.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLineEnd >= 0 && lastFence > firstLineEnd)
            {
                content = content[(firstLineEnd + 1)..lastFence].Trim();
            }
        }

        var start = content.IndexOf('{', StringComparison.Ordinal);
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start
            ? content[start..(end + 1)]
            : string.Empty;
    }

    private static IReadOnlyList<SearchQueryResult> SanitizeSearchQueries(IEnumerable<SearchQueryResponse?>? queries)
    {
        if (queries is null)
        {
            return Array.Empty<SearchQueryResult>();
        }

        var cleanQueries = new List<SearchQueryResult>();
        foreach (var query in queries)
        {
            if (query is null)
            {
                continue;
            }

            var cleanQuery = SanitizeSearchQuery(query.Query);
            if (string.IsNullOrWhiteSpace(cleanQuery) ||
                cleanQueries.Any(existing => string.Equals(existing.Query, cleanQuery, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            cleanQueries.Add(new SearchQueryResult(
                SanitizeSearchLabel(query.Label),
                SanitizeSearchBasis(query.Basis),
                cleanQuery));
            if (cleanQueries.Count >= 3)
            {
                break;
            }
        }

        return cleanQueries;
    }

    private static string SanitizeSearchQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var cleanQuery = query
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        cleanQuery = CompactSpacesBetweenChineseCharacters(cleanQuery);

        if (cleanQuery.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            cleanQuery.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return cleanQuery.Length <= 40 ? cleanQuery : cleanQuery[..40].Trim();
    }

    private static string CompactSpacesBetweenChineseCharacters(string value)
    {
        if (value.IndexOf(' ', StringComparison.Ordinal) < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (current == ' ' &&
                i > 0 &&
                i + 1 < value.Length &&
                IsChineseCharacter(value[i - 1]) &&
                IsChineseCharacter(value[i + 1]))
            {
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    private static bool IsChineseCharacter(char value) =>
        (value >= '\u3400' && value <= '\u4DBF') ||
        (value >= '\u4E00' && value <= '\u9FFF') ||
        (value >= '\uF900' && value <= '\uFAFF');

    private static string SanitizeSearchLabel(string? label)
    {
        label = label?.Trim() ?? string.Empty;
        return label is "closest" or "alternative" or "another_angle"
            ? label
            : "alternative";
    }

    private static string SanitizeSearchBasis(string? basis)
    {
        basis = basis?
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim() ?? string.Empty;
        return basis.Length <= 120 ? basis : basis[..120].Trim();
    }

    private static string ExtractTextPart(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("text", out var textElement) &&
            textElement.ValueKind == JsonValueKind.String)
        {
            return textElement.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static JsonElement ExtractFirstChoiceMessage(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("OpenRouter response did not include any choices.");
        }

        var firstChoice = choices[0];
        if (!firstChoice.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("OpenRouter response did not include a message.");
        }

        return message;
    }

    private static decimal ExtractCost(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) ||
            !usage.TryGetProperty("cost", out var costElement))
        {
            return 0;
        }

        return costElement.ValueKind switch
        {
            JsonValueKind.Number when costElement.TryGetDecimal(out var cost) => cost,
            JsonValueKind.String when decimal.TryParse(costElement.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var cost) => cost,
            _ => 0
        };
    }

    private static TokenUsage ExtractTokenUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
        {
            return new TokenUsage(0, 0, 0);
        }

        return new TokenUsage(
            ExtractInt(usage, "prompt_tokens"),
            ExtractInt(usage, "completion_tokens"),
            ExtractInt(usage, "total_tokens"));
    }

    private static int ExtractInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => 0
        };
    }

    private static string? ExtractString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string ExtractFirstImageDataUrl(JsonElement message)
    {
        return FindFirstImageDataUrl(message);
    }

    private static string FindFirstImageDataUrl(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String &&
            IsImageDataUrl(element.GetString(), out var imageUrl))
        {
            return imageUrl;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nestedImageUrl = FindFirstImageDataUrl(item);
                if (!string.IsNullOrWhiteSpace(nestedImageUrl))
                {
                    return nestedImageUrl;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var nestedImageUrl = FindFirstImageDataUrl(property.Value);
                if (!string.IsNullOrWhiteSpace(nestedImageUrl))
                {
                    return nestedImageUrl;
                }
            }
        }

        return string.Empty;
    }

    private static bool IsImageDataUrl(string? value, out string url)
    {
        url = value?.Trim() ?? string.Empty;
        return url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractProviderError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error))
            {
                return null;
            }

            if (error.ValueKind == JsonValueKind.String)
            {
                return TrimForDisplay(error.GetString() ?? string.Empty);
            }

            if (error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return TrimForDisplay(message.GetString() ?? string.Empty);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static string FormatCost(decimal cost)
    {
        return cost <= 0 ? "$0.00000000" : string.Create(CultureInfo.InvariantCulture, $"${cost:0.00000000}");
    }

    private static string TrimForDisplay(string value)
    {
        value = value.Trim();
        return value.Length <= 500 ? value : value[..500] + "...";
    }

}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json deserialization.")]
internal sealed class TextTranslationResponse
{
    [JsonPropertyName("translation")]
    public string? Translation { get; set; }

    [JsonPropertyName("search_basis")]
    public string? SearchBasis { get; set; }

    [JsonPropertyName("search_queries")]
    public List<SearchQueryResponse?>? SearchQueries { get; set; }
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json deserialization.")]
internal sealed class SearchQueryResponse
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("basis")]
    public string? Basis { get; set; }

    [JsonPropertyName("query")]
    public string? Query { get; set; }
}

internal sealed record ParsedTextTranslation(
    string Text,
    string SearchBasis,
    IReadOnlyList<SearchQueryResult> SearchQueries);

internal sealed record SearchQueryResult(
    string Label,
    string Basis,
    string Query);

internal sealed record TokenUsage(int PromptTokens, int CompletionTokens, int TotalTokens);

internal sealed record TextTranslationResult(
    string Text,
    string SearchBasis,
    IReadOnlyList<SearchQueryResult> SearchQueries,
    decimal CostUsd,
    TokenUsage Usage,
    string? ProviderRequestId);

internal sealed record ImageTranslationResult(
    string ImageData,
    decimal CostUsd,
    TokenUsage Usage,
    string? ProviderRequestId);
