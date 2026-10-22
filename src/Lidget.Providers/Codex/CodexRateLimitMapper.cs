using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lidget.Core.Models;

namespace Lidget.Providers.Codex;

/// <summary>Maps <c>account/rateLimits/read</c> results and updates to limit windows.</summary>
public static class CodexRateLimitMapper
{
    public const string SessionId = "codex.session";
    public const string WeeklyId = "codex.weekly";

    private const int SessionMinutes = 300;
    private const int WeeklyMinutes = 10080;

    /// <summary>
    /// Merges a sparse <c>account/rateLimits/updated</c> snapshot into the last full one: values that the
    /// update omits or sets to null keep their previous value.
    /// </summary>
    public static JsonObject Merge(JsonObject previous, JsonElement update)
    {
        var merged = (JsonObject)previous.DeepClone();
        if (update.ValueKind != JsonValueKind.Object)
        {
            return merged;
        }

        foreach (var property in update.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Object && merged[property.Name] is JsonObject existing)
            {
                merged[property.Name] = Merge(existing, property.Value);
            }
            else
            {
                merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }

        return merged;
    }

    /// <summary>Maps a <c>RateLimitSnapshot</c> object.</summary>
    public static IReadOnlyList<LimitWindow> Map(JsonElement snapshot, DateTimeOffset observedAt)
    {
        var result = new List<LimitWindow>();
        if (snapshot.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        AddWindow(result, snapshot, "primary", LimitKind.Session, observedAt);
        AddWindow(result, snapshot, "secondary", LimitKind.Weekly, observedAt);
        return result;
    }

    public static string? FormatPlan(JsonElement snapshot) =>
        snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("planType", out var plan) && plan.ValueKind == JsonValueKind.String
            ? plan.GetString() switch
            {
                null or "unknown" => null,
                "prolite" => "Pro Lite",
                "promax" => "Pro Max",
                var other => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other.Replace('_', ' ')),
            }
            : null;

    private static void AddWindow(List<LimitWindow> result, JsonElement snapshot, string name, LimitKind fallbackKind, DateTimeOffset observedAt)
    {
        if (!snapshot.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        int? minutes = window.TryGetProperty("windowDurationMins", out var duration) && duration.ValueKind == JsonValueKind.Number
            ? duration.GetInt32()
            : null;
        DateTimeOffset? resetsAt = window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(reset.GetInt64())
            : null;

        var (id, kind, label) = minutes switch
        {
            SessionMinutes => (SessionId, LimitKind.Session, "Session (5h)"),
            WeeklyMinutes => (WeeklyId, LimitKind.Weekly, "Weekly"),
            null => fallbackKind == LimitKind.Session ? (SessionId, LimitKind.Session, "Session") : (WeeklyId, LimitKind.Weekly, "Weekly"),
            _ => ($"codex.{name}", LimitKind.Other, FormatDuration(minutes.Value)),
        };

        result.Add(new LimitWindow(id, kind, label, used.GetDouble(), resetsAt, "app-server", observedAt));
    }

    private static string FormatDuration(int minutes) => minutes % 1440 == 0
        ? $"{minutes / 1440}-day window"
        : minutes % 60 == 0 ? $"{minutes / 60}-hour window" : $"{minutes}-minute window";
}
