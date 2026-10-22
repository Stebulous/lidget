using Lidget.Core.Models;

namespace Lidget.Core.Costs;

/// <summary>Adds request costs into the Today, 7-day, and 30-day totals by local calendar day.</summary>
public sealed class CostAccumulator
{
    public const int WindowDays = 30;

    private readonly TimeZoneInfo _zone;
    private readonly DateOnly _today;
    private readonly SortedSet<string> _unpriced = new(StringComparer.Ordinal);
    private double _todayTotal;
    private double _week;
    private double _month;

    public CostAccumulator(DateTimeOffset now, TimeZoneInfo zone)
    {
        _zone = zone;
        _today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
    }

    /// <summary>The start of the 30-day window: local midnight 29 days before today.</summary>
    public static DateTimeOffset WindowStart(DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var midnight = today.AddDays(-(WindowDays - 1)).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }

    public void Add(DateTimeOffset timestamp, double cost)
    {
        var age = _today.DayNumber - DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, _zone).DateTime).DayNumber;
        if (age is < 0 or >= WindowDays)
        {
            return;
        }

        _month += cost;
        if (age < 7)
        {
            _week += cost;
        }

        if (age == 0)
        {
            _todayTotal += cost;
        }
    }

    /// <summary>Records a model that the pricing table cannot price. Its tokens are not in the totals.</summary>
    public void AddUnpriced(string model) => _unpriced.Add(model);

    public CostSummary Build() => new(Money(_todayTotal), Money(_week), Money(_month), "USD")
    {
        UnpricedModels = [.. _unpriced],
    };

    private static decimal Money(double value) => Math.Round((decimal)value, 4);
}
