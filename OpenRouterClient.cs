using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json;

namespace Peek;

public static class OpenRouterClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);
    private static readonly HttpClient HttpClient = new()
    {
        BaseAddress = new Uri("https://openrouter.ai/")
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

        var fromLanguage = string.IsNullOrWhiteSpace(config.FromLanguage) ? "Chinese" : config.FromLanguage.Trim();
        var toLanguage = string.IsNullOrWhiteSpace(config.ToLanguage) ? "English" : config.ToLanguage.Trim();
        var imageDataUrl = ToPngDataUrl(bitmap);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
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
                            text = $"Read the visible game UI text in this image and translate it from {fromLanguage} to {toLanguage}. Preserve names, numbers, item names, skill names, currencies, punctuation, and UI labels where appropriate. Return only the translated text. Keep line breaks when useful."
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

        AppLogger.Info($"operation={operationId} openrouter.request model={model} from={fromLanguage} to={toLanguage} capture={bitmap.Width}x{bitmap.Height}");

        var (statusCode, body) = await SendCompletionAsync(config, payload, cancellationToken).ConfigureAwait(false);
        var root = ParseSuccessfulResponse(statusCode, body, operationId);
        var message = ExtractFirstChoiceMessage(root);
        var translation = ExtractContent(message);
        var providerRequestId = ExtractString(root, "id");
        var cost = ExtractCost(root);
        var usage = ExtractTokenUsage(root);
        LogUsage(operationId, providerRequestId, cost, usage);

        if (string.IsNullOrWhiteSpace(translation))
        {
            throw new InvalidOperationException("No translation returned. See log.");
        }

        return new TextTranslationResult(translation.Trim(), cost, usage, providerRequestId);
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

        var fromLanguage = string.IsNullOrWhiteSpace(config.FromLanguage) ? "Chinese" : config.FromLanguage.Trim();
        var toLanguage = string.IsNullOrWhiteSpace(config.ToLanguage) ? "English" : config.ToLanguage.Trim();
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
                            text = $"Edit this game UI screenshot by replacing visible {fromLanguage} text with natural {toLanguage}. Preserve the original UI, background, icons, layout, colors, proportions, and game art. Only change the text. Return the edited image."
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

        AppLogger.Info($"operation={operationId} openrouter.request mode=image_edit model={model} from={fromLanguage} to={toLanguage} capture={bitmap.Width}x{bitmap.Height}");

        var (statusCode, body) = await SendCompletionAsync(config, payload, cancellationToken).ConfigureAwait(false);
        var root = ParseSuccessfulResponse(statusCode, body, operationId);
        var message = ExtractFirstChoiceMessage(root);
        var imageUrl = ExtractFirstImageDataUrl(message);
        var providerRequestId = ExtractString(root, "id");
        var cost = ExtractCost(root);
        var usage = ExtractTokenUsage(root);
        LogUsage(operationId, providerRequestId, cost, usage);

        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            throw new InvalidOperationException("No edited image returned. See log.");
        }

        return new ImageTranslationResult(imageUrl, cost, usage, providerRequestId);
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

    private static string ExtractContent(JsonElement message)
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
            return contentElement.ToString();
        }

        var parts = new List<string>();
        foreach (var item in contentElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                parts.Add(item.GetString() ?? string.Empty);
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("text", out var textElement) &&
                textElement.ValueKind == JsonValueKind.String)
            {
                parts.Add(textElement.GetString() ?? string.Empty);
            }
        }

        return string.Join(Environment.NewLine, parts);
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
        if (message.TryGetProperty("images", out var images) &&
            images.ValueKind == JsonValueKind.Array)
        {
            foreach (var image in images.EnumerateArray())
            {
                if (TryExtractImageDataUrl(image, "image_url", out var url) ||
                    TryExtractImageDataUrl(image, "imageUrl", out url))
                {
                    return url;
                }
            }
        }

        if (!message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String &&
                IsImageDataUrl(part.GetString(), out var stringUrl))
            {
                return stringUrl;
            }

            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (TryExtractImageDataUrl(part, "image_url", out var url) ||
                TryExtractImageDataUrl(part, "imageUrl", out url))
            {
                return url;
            }

            if (part.TryGetProperty("url", out var urlElement) &&
                urlElement.ValueKind == JsonValueKind.String &&
                IsImageDataUrl(urlElement.GetString(), out url))
            {
                return url;
            }
        }

        return string.Empty;
    }

    private static bool TryExtractImageDataUrl(JsonElement image, string propertyName, out string url)
    {
        url = string.Empty;
        if (!image.TryGetProperty(propertyName, out var imageUrl))
        {
            return false;
        }

        if (imageUrl.ValueKind == JsonValueKind.String)
        {
            return IsImageDataUrl(imageUrl.GetString(), out url);
        }

        return imageUrl.ValueKind == JsonValueKind.Object &&
            imageUrl.TryGetProperty("url", out var urlElement) &&
            urlElement.ValueKind == JsonValueKind.String &&
            IsImageDataUrl(urlElement.GetString(), out url);
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

public sealed record TokenUsage(int PromptTokens, int CompletionTokens, int TotalTokens);

public sealed record TextTranslationResult(
    string Text,
    decimal CostUsd,
    TokenUsage Usage,
    string? ProviderRequestId);

public sealed record ImageTranslationResult(
    string ImageData,
    decimal CostUsd,
    TokenUsage Usage,
    string? ProviderRequestId);
