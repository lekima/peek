using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

namespace Peek;

public sealed class OpenRouterClient
{
    private static readonly HttpClient HttpClient = new()
    {
        BaseAddress = new Uri("https://openrouter.ai/")
    };

    public async Task<TextTranslationResult> TranslateImageToTextAsync(
        Bitmap bitmap,
        AppConfig config,
        string operationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new InvalidOperationException("OpenRouter API key is missing.");
        }

        var fromLanguage = string.IsNullOrWhiteSpace(config.FromLanguage) ? "Chinese" : config.FromLanguage.Trim();
        var toLanguage = string.IsNullOrWhiteSpace(config.ToLanguage) ? "Vietnamese" : config.ToLanguage.Trim();
        var imageDataUrl = ToPngDataUrl(bitmap);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = config.Model,
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

        AppLogger.Info($"operation={operationId} openrouter.request model={config.Model} from={fromLanguage} to={toLanguage} capture={bitmap.Width}x{bitmap.Height}");

        var (statusCode, body) = await SendCompletionAsync(config, payload, cancellationToken);
        AppLogger.Info(
            (int)statusCode >= 200 && (int)statusCode < 300
                ? $"operation={operationId} openrouter.response status={(int)statusCode}"
                : $"operation={operationId} openrouter.response status={(int)statusCode} body={TrimForDebug(body)}");

        if ((int)statusCode < 200 || (int)statusCode >= 300)
        {
            throw new InvalidOperationException($"OpenRouter error {(int)statusCode}: {TrimForDisplay(body)}");
        }

        using var document = JsonDocument.Parse(body);
        var choice = document.RootElement.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var translation = ExtractContent(message);
        var providerRequestId = ExtractString(document.RootElement, "id");
        var cost = ExtractCost(document.RootElement);
        var usage = ExtractTokenUsage(document.RootElement);
        AppLogger.Info(
            $"operation={operationId} openrouter.usage " +
            $"provider_request_id={providerRequestId ?? "-"} " +
            $"cost={FormatCost(cost)} " +
            $"prompt_tokens={usage.PromptTokens} " +
            $"completion_tokens={usage.CompletionTokens} " +
            $"total_tokens={usage.TotalTokens}");

        if (string.IsNullOrWhiteSpace(translation))
        {
            throw new InvalidOperationException("No translation returned. See log.");
        }

        return new TextTranslationResult(translation.Trim(), cost, usage, providerRequestId);
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

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
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
            JsonValueKind.String when decimal.TryParse(costElement.GetString(), out var cost) => cost,
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
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => 0
        };
    }

    private static string? ExtractString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    public static string FormatCost(decimal cost)
    {
        return cost <= 0 ? "$0.00000000" : $"${cost:0.00000000}";
    }

    private static string TrimForDisplay(string value)
    {
        value = value.Trim();
        return value.Length <= 500 ? value : value[..500] + "...";
    }

    private static string TrimForDebug(string value)
    {
        value = value.Trim();
        return value.Length <= 1200 ? value : value[..1200] + "...";
    }
}

public sealed record TokenUsage(int PromptTokens, int CompletionTokens, int TotalTokens);

public sealed record TextTranslationResult(
    string Text,
    decimal CostUsd,
    TokenUsage Usage,
    string? ProviderRequestId);
