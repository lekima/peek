using System.IO;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Peek;

internal static class AppLogger
{
    private const long MaxLogBytes = 25 * 1024 * 1024;
    private const int MaxArchiveFiles = 50;
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static string LogDirectory =>
        AppPaths.DataDirectory;

    public static string LogPath => Path.Combine(LogDirectory, "peek.log.jsonl");

    public static void Event(string eventName, object data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(data);

        WriteJson(new
        {
            timestamp = DateTimeOffset.Now,
            level = "info",
            eventName,
            data
        });
    }

    public static void Info(string message)
    {
        WriteJson(new
        {
            timestamp = DateTimeOffset.Now,
            level = "info",
            eventName = "message",
            message
        });
    }

    public static void Error(string message, Exception? exception = null)
    {
        WriteJson(new
        {
            timestamp = DateTimeOffset.Now,
            level = "error",
            eventName = "error",
            message,
            error = exception?.ToString()
        });
    }

    public static void Usage(UsageLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        WriteJson(new
        {
            timestamp = entry.Timestamp,
            level = "info",
            eventName = "usage",
            operationId = entry.OperationId,
            providerRequestId = entry.ProviderRequestId,
            model = entry.Model,
            resultFormat = entry.ResultFormat,
            targetLanguage = entry.TargetLanguage,
            targetGame = entry.TargetGame,
            searchProfile = entry.SearchProfile,
            searchSource = entry.SearchSource,
            searchLanguage = entry.SearchLanguage,
            searchPrefix = entry.SearchPrefix,
            success = entry.Success,
            costUsd = entry.CostUsd,
            totalCostUsd = entry.TotalCostUsd,
            durationMs = entry.DurationMs,
            captureWidth = entry.CaptureWidth,
            captureHeight = entry.CaptureHeight,
            promptTokens = entry.PromptTokens,
            completionTokens = entry.CompletionTokens,
            totalTokens = entry.TotalTokens,
            errorKind = entry.ErrorKind,
            errorMessage = entry.ErrorMessage
        });
    }

    public static void TextResult(TextResultLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        WriteJson(new
        {
            timestamp = entry.Timestamp,
            level = "info",
            eventName = "text_result",
            operationId = entry.OperationId,
            mode = "text",
            model = entry.Model,
            targetLanguage = entry.TargetLanguage,
            targetGame = entry.TargetGame,
            searchProfile = entry.SearchProfile,
            searchSource = entry.SearchSource,
            searchLanguage = entry.SearchLanguage,
            searchPrefix = entry.SearchPrefix,
            capturePath = entry.CapturePath,
            translation = entry.Translation,
            searchBasis = entry.SearchBasis,
            searchQueries = entry.SearchQueries
        });
    }

    public static void Capture(CaptureLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        WriteJson(new
        {
            timestamp = entry.Timestamp,
            level = "info",
            eventName = "capture",
            operationId = entry.OperationId,
            mode = entry.Mode,
            path = entry.Path,
            width = entry.Width,
            height = entry.Height,
            frameWidth = entry.FrameWidth,
            frameHeight = entry.FrameHeight
        });
    }

    public static void SearchClick(SearchClickLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        WriteJson(new
        {
            timestamp = entry.Timestamp,
            level = "info",
            eventName = "search_click",
            operationId = entry.OperationId,
            index = entry.Index,
            label = entry.Label,
            query = entry.Query,
            basis = entry.Basis,
            targetGame = entry.TargetGame,
            searchProfile = entry.SearchProfile,
            searchSource = entry.SearchSource,
            searchLanguage = entry.SearchLanguage,
            searchPrefix = entry.SearchPrefix,
            url = entry.Url
        });
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Logging must never interrupt the overlay or translation flow.")]
    private static void WriteJson<T>(T entry)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateLogIfNeeded();
                File.AppendAllText(LogPath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never interrupt the overlay or a translation result.
        }
    }

    private static void RotateLogIfNeeded()
    {
        var file = new FileInfo(LogPath);
        if (!file.Exists || file.Length < MaxLogBytes)
        {
            return;
        }

        for (var i = MaxArchiveFiles; i >= 1; i--)
        {
            var sourcePath = i == 1
                ? LogPath
                : Path.Combine(LogDirectory, $"peek.{i - 1}.log.jsonl");
            var destinationPath = Path.Combine(LogDirectory, $"peek.{i}.log.jsonl");

            if (!File.Exists(sourcePath))
            {
                continue;
            }

            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            File.Move(sourcePath, destinationPath);
        }
    }
}

internal sealed record UsageLogEntry(
    DateTimeOffset Timestamp,
    string OperationId,
    string? ProviderRequestId,
    string Model,
    string ResultFormat,
    string TargetLanguage,
    string TargetGame,
    string SearchProfile,
    string SearchSource,
    string SearchLanguage,
    string SearchPrefix,
    bool Success,
    decimal CostUsd,
    decimal TotalCostUsd,
    long DurationMs,
    int CaptureWidth,
    int CaptureHeight,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    string? ErrorKind,
    string? ErrorMessage);

internal sealed record TextResultLogEntry(
    DateTimeOffset Timestamp,
    string OperationId,
    string Model,
    string TargetLanguage,
    string TargetGame,
    string SearchProfile,
    string SearchSource,
    string SearchLanguage,
    string SearchPrefix,
    string? CapturePath,
    string Translation,
    string SearchBasis,
    IReadOnlyList<SearchQueryResult> SearchQueries);

internal sealed record CaptureLogEntry(
    DateTimeOffset Timestamp,
    string OperationId,
    string Mode,
    string Path,
    int Width,
    int Height,
    double FrameWidth,
    double FrameHeight);

internal sealed record SearchClickLogEntry(
    DateTimeOffset Timestamp,
    string OperationId,
    int Index,
    string Label,
    string Query,
    string Basis,
    string TargetGame,
    string SearchProfile,
    string SearchSource,
    string SearchLanguage,
    string SearchPrefix,
    string Url);
