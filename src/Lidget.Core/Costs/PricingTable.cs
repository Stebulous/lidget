using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Lidget.Core.Costs;

/// <summary>
/// Model prices from <c>assets/pricing.json</c>. The resolution order follows OpenUsage
/// (<c>ModelPricing.swift</c>, MIT): alias rules, exact name, <c>-fast</c> variant from its base model,
/// the name without a provider prefix or date suffix, then the longest catalog name that the model
/// name starts with at a word boundary.
/// </summary>
public sealed partial class PricingTable
{
    private const string FastSuffix = "-fast";

    private readonly IReadOnlyDictionary<string, ModelRates> _models;
    private readonly IReadOnlyList<(Regex Pattern, string Canonical)> _aliases;
    private readonly ConcurrentDictionary<string, ModelRates?> _memo = new(StringComparer.Ordinal);

    public PricingTable(IReadOnlyDictionary<string, ModelRates> models, IReadOnlyList<(Regex Pattern, string Canonical)>? aliases = null)
    {
        _models = models;
        _aliases = aliases ?? [];
    }

    public string? Source { get; private init; }

    public static PricingTable Load(Stream json)
    {
        var document = JsonSerializer.Deserialize(json, PricingJsonContext.Default.PricingDocument)
            ?? throw new InvalidDataException("The pricing file is empty.");

        var models = document.Models.ToDictionary(
            pair => pair.Key,
            pair => new ModelRates(pair.Value.Input, pair.Value.Output, pair.Value.CacheWrite ?? pair.Value.Input, pair.Value.CacheRead ?? pair.Value.Input)
            {
                Fast = pair.Value.Fast ?? 1,
                Ultrafast = pair.Value.Ultrafast,
                LongContext = pair.Value.LongContext is { } lc
                    ? new LongContextRates(lc.Threshold, lc.Input, lc.Output, lc.CacheWrite, lc.CacheRead)
                    : null,
            },
            StringComparer.Ordinal);

        var aliases = document.Aliases
            .Select(a => (new Regex(a.Pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), a.Canonical))
            .ToList();

        return new PricingTable(models, aliases) { Source = document.Source };
    }

    /// <summary>Loads the pricing table that ships inside <c>Lidget.Core</c>.</summary>
    public static PricingTable LoadDefault()
    {
        using var stream = typeof(PricingTable).Assembly.GetManifestResourceStream("Lidget.pricing.json")
            ?? throw new InvalidOperationException("The pricing resource is missing.");
        return Load(stream);
    }

    /// <summary>The alias target of <paramref name="model"/>, or the model name itself.</summary>
    public string CanonicalName(string model)
    {
        foreach (var (pattern, canonical) in _aliases)
        {
            if (pattern.IsMatch(model))
            {
                return canonical;
            }
        }

        return model;
    }

    /// <summary>The rates of <paramref name="model"/>, or null when no entry can price it.</summary>
    public ModelRates? Resolve(string model) => _memo.GetOrAdd(model.Trim(), ResolveUncached);

    private ModelRates? ResolveUncached(string model)
    {
        var canonical = CanonicalName(model);
        return canonical != model ? Lookup(canonical) ?? Lookup(model) : Lookup(model);
    }

    private ModelRates? Lookup(string name)
    {
        if (_models.TryGetValue(name, out var exact))
        {
            return exact;
        }

        if (name.EndsWith(FastSuffix, StringComparison.Ordinal))
        {
            // Price a fast variant only from a known multiplier, never at the standard rate.
            var baseRates = Lookup(name[..^FastSuffix.Length]);
            return baseRates is { Fast: not 1 } ? baseRates.Scaled(baseRates.Fast) : null;
        }

        var normalized = Normalize(name);
        if (normalized != name && _models.TryGetValue(normalized, out var plain))
        {
            return plain;
        }

        return FindByPrefix(normalized);
    }

    /// <summary>Removes a provider prefix such as <c>anthropic/</c> and a date suffix.</summary>
    private static string Normalize(string name)
    {
        var slash = name.LastIndexOf('/');
        var bare = slash >= 0 ? name[(slash + 1)..] : name;
        return DateSuffix().Replace(bare, string.Empty);
    }

    private ModelRates? FindByPrefix(string name)
    {
        // "claude-sonnet-4-5-thinking" matches "claude-sonnet-4-5". Keys with a date suffix also match
        // their undated name: "claude-sonnet-4" matches "claude-sonnet-4-20250514".
        string? best = null;
        foreach (var key in _models.Keys)
        {
            var plainKey = DateSuffix().Replace(key, string.Empty);
            var isPrefix = name.StartsWith(plainKey, StringComparison.Ordinal)
                && (name.Length == plainKey.Length || name[plainKey.Length] is '-' or '.' or ':' or '@' or '[');
            if (isPrefix && (best is null || plainKey.Length > DateSuffix().Replace(best, string.Empty).Length))
            {
                best = key;
            }
        }

        return best is null ? null : _models[best];
    }

    [GeneratedRegex(@"-(\d{4}-\d{2}-\d{2}|\d{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex DateSuffix();

    internal sealed record PricingDocument(
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("models")] Dictionary<string, PricingModel> Models,
        [property: JsonPropertyName("aliases")] List<PricingAlias> Aliases);

    internal sealed record PricingModel(
        double Input,
        double Output,
        double? CacheWrite,
        double? CacheRead,
        double? Fast,
        double? Ultrafast,
        PricingLongContext? LongContext);

    internal sealed record PricingLongContext(long Threshold, double? Input, double? Output, double? CacheWrite, double? CacheRead);

    internal sealed record PricingAlias(string Pattern, string Canonical);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(PricingTable.PricingDocument))]
internal sealed partial class PricingJsonContext : JsonSerializerContext;
