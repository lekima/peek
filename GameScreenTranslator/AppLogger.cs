using System.IO;
using System.Text.Json;

namespace Peek;

public static class AppLogger
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static string LogDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Peek");

    public static string LogPath => Path.Combine(LogDirectory, "peek.log.jsonl");

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
        WriteJson(new
        {
            timestamp = entry.Timestamp,
            level = "info",
            eventName = "usage",
            operationId = entry.OperationId,
            providerRequestId = entry.ProviderRequestId,
            model = entry.Model,
            fromLanguage = entry.FromLanguage,
            toLanguage = entry.ToLanguage,
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

    private static void WriteJson<T>(T entry)
    {
        lock (Lock)
        {
            Directory.CreateDirectory(LogDirectory);
            RotateLogIfNeeded();
            File.AppendAllText(LogPath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
        }
    }

    private static void RotateLogIfNeeded()
    {
        var file = new FileInfo(LogPath);
        if (!file.Exists || file.Length < MaxLogBytes)
        {
            return;
        }

        var archivePath = Path.Combine(LogDirectory, "peek.previous.log.jsonl");
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        File.Move(LogPath, archivePath);
    }
}

public sealed record UsageLogEntry(
    DateTimeOffset Timestamp,
    string OperationId,
    string? ProviderRequestId,
    string Model,
    string FromLanguage,
    string ToLanguage,
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
