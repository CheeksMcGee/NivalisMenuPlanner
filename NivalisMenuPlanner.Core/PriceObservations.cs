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
/// </summary>
public sealed class PriceObservations
{
    readonly Dictionary<string, List<(string district, int priceHundredths)>> _byDish =
        new(StringComparer.OrdinalIgnoreCase);

    public int ObservationCount { get; private set; }
    public int DistrictCount { get; private set; }

    public void Add(string dishId, string district, int priceHundredths)
    {
        if (string.IsNullOrEmpty(dishId) || priceHundredths <= 0) return;
        if (!_byDish.TryGetValue(dishId, out var list))
            _byDish[dishId] = list = new List<(string, int)>();
        list.Add((district, priceHundredths));
        ObservationCount++;
    }

    /// <summary>Every observed price for a dish, all districts.</summary>
    public IReadOnlyList<int> PricesOf(string dishId) =>
        _byDish.TryGetValue(dishId, out var list)
            ? list.Select(x => x.priceHundredths).ToList()
            : new List<int>();

    /// <summary>Observed price range for a dish within one district, or null if unseen there.</summary>
    public (int min, int max)? RangeIn(string dishId, string district)
    {
        if (!_byDish.TryGetValue(dishId, out var list)) return null;
        var inDistrict = list.Where(x => x.district.Equals(district, StringComparison.OrdinalIgnoreCase))
                             .Select(x => x.priceHundredths).ToList();
        return inDistrict.Count == 0 ? null : (inDistrict.Min(), inDistrict.Max());
    }

    /// <summary>
    /// The measured cap for a dish: 99th-percentile citywide observed price.
    /// Percentile rather than max because one bugged NPC menu shouldn't become
    /// the ceiling (the same robustness logic as the escrow's honesty guards).
    /// Null when the dish has no observations - the CSV estimate stands.
    /// </summary>
    public int? CapOf(string dishId)
    {
        if (!_byDish.TryGetValue(dishId, out var list) || list.Count == 0) return null;
        var sorted = list.Select(x => x.priceHundredths).OrderBy(p => p).ToList();
        // nearest-rank percentile: index = ceil(0.99 * n) - 1, clamped
        int idx = Math.Min(sorted.Count - 1, (int)Math.Ceiling(0.99 * sorted.Count) - 1);
        return sorted[idx];
    }

    /// <summary>Dishes with at least one observation.</summary>
    public IReadOnlyList<string> ObservedDishes => _byDish.Keys.ToList();
}