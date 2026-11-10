using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lidget.Core.Models;

namespace Lidget.Providers.Claude;

/// <summary>
/// Maps the <c>cedar_ember</c> block of the <c>/api/oauth/usage</c> response to banked resets (plan decision
/// D58). The block and its rules are a port of <c>claudeResetCredits.ts</c> in T3 Code (MIT). The block is
/// undocumented: if it is missing or has a different shape, Lidget has no reset data.
/// </summary>
public static partial class ClaudeResetMapper
{
    /// <summary>The name of the block in the response, and the <c>program</c> value of a redemption request.</summary>
    public const string Program = "cedar_ember";

    /// <summary>Returns the banked resets, or null when the response has no reset data for the account.</summary>
    public static ResetSummary? Map(JsonElement body, DateTimeOffset now)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty(Program, out var block) || block.ValueKind != JsonValueKind.Object
            || !block.TryGetProperty("eligible", out var eligible) || eligible.ValueKind != JsonValueKind.True)
        {
            return null;
        }

        var nextId = block.TryGetProperty("next_grant_id", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        var grants = new List<BankedReset>();
        if (block.TryGetProperty("grants", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (MapGrant(entry, now) is { } grant)
                {
                    grants.Add(grant);
                }
            }
        }

        // Claude redeems only the next grant. Without a usable next grant, no reset is available now.
        var hasNext = grants.Any(g => g.IsUsable && g.Id == nextId);
        var resets = grants.Select(g => g with { CanRedeemNow = hasNext && g.Id == nextId }).ToArray();
        return new ResetSummary(hasNext ? resets.Where(g => g.IsUsable).Sum(g => g.Count) : 0, resets, now)
        {
            NextResetId = hasNext ? nextId : null,
        };
    }

    /// <summary>Whether <paramref name="id"/> has the form of a grant ID. Other values are not sent to the server.</summary>
    public static bool IsGrantId(string? id) => id is not null && GrantId().IsMatch(id);

    private static BankedReset? MapGrant(JsonElement entry, DateTimeOffset now)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || !IsGrantId(id.GetString())
            || !entry.TryGetProperty("resets_left", out var left) || left.ValueKind != JsonValueKind.Number
            || !left.TryGetInt32(out var count) || count <= 0)
        {
            return null;
        }

        DateTimeOffset? expiresAt = null;
        if (entry.TryGetProperty("ends_at", out var ends) && ends.ValueKind != JsonValueKind.Null)
        {
            // A grant with an end time that Lidget cannot read, or that passed, is not available.
            if (ends.ValueKind != JsonValueKind.String || ParseTime(ends.GetString()!) is not { } time || time <= now)
            {
                return null;
            }

            expiresAt = time;
        }

        var paused = entry.TryGetProperty("paused", out var p) && p.ValueKind == JsonValueKind.True;
        var usableNow = entry.TryGetProperty("usable_now", out var u) && u.ValueKind == JsonValueKind.True;
        var startsAt = entry.TryGetProperty("starts_at", out var starts) && starts.ValueKind == JsonValueKind.String ? ParseTime(starts.GetString()!) : null;
        return new BankedReset(id.GetString()!, Label(entry), count, startsAt, expiresAt) { IsUsable = usableNow && !paused };
    }

    /// <summary>The <c>label</c> of a grant, for example "Free limit reset", or null. T3 Code does not show it.</summary>
    private static string? Label(JsonElement entry)
    {
        if (!entry.TryGetProperty("label", out var label) || label.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = string.Join(' ', label.GetString()!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length switch
        {
            0 => null,
            > MaxLabelLength => text[..MaxLabelLength].TrimEnd() + "…",
            _ => text,
        };
    }

    /// <summary>A longer label is cut, so that a changed server value cannot fill the flyout.</summary>
    private const int MaxLabelLength = 120;

    private static DateTimeOffset? ParseTime(string text) =>
        CompleteTimestamp().IsMatch(text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            ? time
            : null;

    [GeneratedRegex("^[a-z0-9_-]{1,40}$")]
    private static partial Regex GrantId();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex CompleteTimestamp();
}
