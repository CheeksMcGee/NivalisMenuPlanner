using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisMenuPlanner.Core;

/// <summary>
/// Real menu prices observed across the city (NPC venues included), harvested at
/// GameReady via the kit's MenuOf. The CSV's estimated caps (input cost x type
/// factor) retire the moment this catalog has data: the 99th-percentile observed
/// price per dish becomes the planner's cap. District spreads are kept for the
/// future district-demand calibration; the cap derivation uses citywide max.
/// 5f-2: observations carry the venue key, and district medians EXCLUDE the
/// caller's own venues - your price should never be your market benchmark
/// (the Eyeball-at-31.00 finding: own-venue prices had become the district
/// median for dishes only the player serves).
/// </summary>
public sealed class PriceObservations
{
    readonly Dictionary<string, List<(string district, int priceHundredths, string venue)>> _byDish =
        new(StringComparer.OrdinalIgnoreCase);

    public int ObservationCount { get; private set; }

    public void Add(string dishId, string district, int priceHundredths, string? venue = null)
    {
        if (string.IsNullOrEmpty(dishId) || priceHundredths <= 0) return;
        if (!_byDish.TryGetValue(dishId, out var list))
            _byDish[dishId] = list = new List<(string, int, string)>();
        list.Add((district, priceHundredths, venue ?? "?"));
        ObservationCount++;
    }

    /// <summary>Every observed price for a dish, all districts.</summary>
    public IReadOnlyList<int> PricesOf(string dishId) =>
        _byDish.TryGetValue(dishId, out var list)
            ? list.Select(x => x.priceHundredths).ToList()
            : new List<int>();

    /// <summary>
    /// Observed price range for a dish within one district, EXCLUDING the caller's
    /// own venues (self-anchor fix). Null when the district has no non-own
    /// observations - the thin-coverage fallback (crawl only) engages.
    /// </summary>
    public (int min, int max)? RangeIn(string dishId, string district, IReadOnlyCollection<string>? ownVenues = null)
    {
        if (!_byDish.TryGetValue(dishId, out var list)) return null;
        var inDistrict = list
            .Where(x => x.district.Equals(district, StringComparison.OrdinalIgnoreCase))
            .Where(x => ownVenues == null || !ownVenues.Contains(x.venue))
            .Select(x => x.priceHundredths).ToList();
        return inDistrict.Count == 0 ? null : (inDistrict.Min(), inDistrict.Max());
    }

    /// <summary>Backward-compatible overload: unfiltered range (NPC + own).</summary>
    public (int min, int max)? RangeIn(string dishId, string district) =>
        RangeIn(dishId, district, null);

    /// <summary>
    /// The measured cap for a dish: 99th-percentile citywide observed price,
    /// ALL venues included (caps are ceilings, not benchmarks - your own price
    /// legitimately contributes to what the market tolerates citywide).
    /// </summary>
    public int? CapOf(string dishId)
    {
        if (!_byDish.TryGetValue(dishId, out var list) || list.Count == 0) return null;
        var sorted = list.Select(x => x.priceHundredths).OrderBy(p => p).ToList();
        int idx = Math.Min(sorted.Count - 1, (int)Math.Ceiling(0.99 * sorted.Count) - 1);
        return sorted[idx];
    }

    public IReadOnlyList<string> ObservedDishes => _byDish.Keys.ToList();
}