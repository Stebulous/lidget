using System.Globalization;
using System.Text.Json;
using Lidget.Core.Models;

namespace Lidget.Providers.Claude;

/// <summary>Maps the <c>/api/oauth/usage</c> response to limit windows.</summary>
public static class ClaudeUsageMapper
{
    public const string SessionId = "claude.session";
    public const string WeeklyId = "claude.weekly";

    public static IReadOnlyList<LimitWindow> Map(JsonElement body, DateTimeOffset observedAt)
    {
        // The `limits` array is the current form. The legacy keys are a fallback for older responses.
        var limits = MapLimitsArray(body, observedAt);
        return limits.Count > 0 ? limits : MapLegacyKeys(body, observedAt);
    }

    public static string? FormatPlan(string? subscriptionType, string? rateLimitTier)
    {
        if (string.IsNullOrWhiteSpace(subscriptionType))
        {
            return null;
        }

        var plan = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(subscriptionType.Trim().ToLowerInvariant());
        var multiplier = rateLimitTier is null ? null : System.Text.RegularExpressions.Regex.Match(rateLimitTier, @"\d+x").Value;
        return string.IsNullOrEmpty(multiplier) ? plan : $"{plan} {multiplier}";
    }

    private static List<LimitWindow> MapLimitsArray(JsonElement body, DateTimeOffset observedAt)
    {
        var result = new List<LimitWindow>();
        if (!body.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var entry in limits.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || ReadNumber(entry, "percent") is not { } percent)
            {
                continue;
            }

            var resetsAt = ReadDate(entry, "resets_at");
            switch (ReadString(entry, "kind"))
            {
                case "session":
                    result.Add(new LimitWindow(SessionId, LimitKind.Session, "Session (5h)", percent, resetsAt, "endpoint", observedAt));
                    break;
                case "weekly_all":
                    result.Add(new LimitWindow(WeeklyId, LimitKind.Weekly, "Weekly", percent, resetsAt, "endpoint", observedAt));
                    break;
                case "weekly_scoped" when ScopedModelName(entry) is { } model:
                    result.Add(new LimitWindow(
                        $"{WeeklyId}.{model.ToLowerInvariant().Replace(' ', '-')}",
                        LimitKind.Other,
                        $"Weekly ({model})",
                        percent,
                        resetsAt,
                        "endpoint",
                        observedAt));
                    break;
            }
        }

        return result;
    }

    private static List<LimitWindow> MapLegacyKeys(JsonElement body, DateTimeOffset observedAt)
    {
        var result = new List<LimitWindow>();
        if (Window(body, "five_hour") is { } session)
        {
            result.Add(new LimitWindow(SessionId, LimitKind.Session, "Session (5h)", session.Used, session.ResetsAt, "endpoint", observedAt));
        }

        if (Window(body, "seven_day") is { } weekly)
        {
            result.Add(new LimitWindow(WeeklyId, LimitKind.Weekly, "Weekly", weekly.Used, weekly.ResetsAt, "endpoint", observedAt));
        }

        return result;
    }

    private static (double Used, DateTimeOffset? ResetsAt)? Window(JsonElement body, string name) =>
        body.TryGetProperty(name, out var window) && window.ValueKind == JsonValueKind.Object && ReadNumber(window, "utilization") is { } used
            ? (used, ReadDate(window, "resets_at"))
            : null;

    private static string? ScopedModelName(JsonElement entry) =>
        entry.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
        && scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object
            ? ReadString(model, "display_name")
            : null;

    private static double? ReadNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static DateTimeOffset? ReadDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String when DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) => date,
            // Unix seconds or milliseconds.
            JsonValueKind.Number when value.GetDouble() is var number => number < 1e10
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(number * 1000))
                : DateTimeOffset.FromUnixTimeMilliseconds((long)number),
            _ => null,
        };
    }
}
