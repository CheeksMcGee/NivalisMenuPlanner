using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisMenuPlanner.Core;

public sealed record PriceSuggestion(
    string DishId, int CurrentPriceHundredths, int SuggestedPriceHundredths,
    string Diagnosis, string Rationale);

public static class PricingController
{
    // ---- Doctrine defaults (the session's "config-open" ruling: these become
    // config entries in 5d-2; Core takes them as parameters so the plugin can
    // feed whatever the operator dials in. Defaults are the goat-calibrated values.) ----
    public const int DefaultCrawlStepBps = 500;
    public const int DefaultStickyDownTriggerDays = 2;
    public const int DefaultSlumpThresholdPct = 20;
    public const int DefaultFloorMarginHundredths = 200;
    public const int DefaultAnchorAtMarketPct = 10;
    public const int DefaultDistrictFloorBps = 9500;
    public const int DefaultMinVolumeGate = 3;
    public const bool DefaultRoundToWholeLims = true;

    /// <summary>
    /// One dish's suggestion, given everything the doctrine knows. Pure.
    /// Q2 ladder unchanged; new since 5b per session findings:
    ///  - MIN-VOLUME GATE (both levers): a dish whose demand-clean trailing average
    ///    is below MinVolumeGate is too thin to trend - no slump cuts (Roaches x2,
    ///    Sweet Waffle), no headroom crawls (Roaches' 30.45 raise on 1/day demand).
    ///    The profit bar, not the price, owns dying dishes.
    ///  - WHOLE-LIM QUANTIZATION (direction-preserve): the player's edit UI snaps
    ///    prices to whole lims (29.40 -> 29 on apply; operator finding). Suggestions
    ///    quantize at generation so the applier never rounds against the doctrine's
    ///    direction: crawl-up rounds to the NEXT WHOLE LIM at-or-above; cuts round
    ///    to the WHOLE LIM at-or-below. RoundToWholeLims = false preserves hundredths
    ///    for the manager-era auto-apply (pending the fractional-consumption test).
    /// </summary>
    public static PriceSuggestion? Suggest(
        DishInfo dish, int currentPrice, IReadOnlyList<SaleRecord> records,
        int costHundredths, int platableToday, int? districtMedian, bool established,
        int minVolumeGate = DefaultMinVolumeGate,
        int crawlStepBps = DefaultCrawlStepBps,
        int stickyDownTriggerDays = DefaultStickyDownTriggerDays,
        int slumpThresholdPct = DefaultSlumpThresholdPct,
        int floorMarginHundredths = DefaultFloorMarginHundredths,
        int anchorAtMarketPct = DefaultAnchorAtMarketPct,
        int districtFloorBps = DefaultDistrictFloorBps,
        bool roundToWholeLims = DefaultRoundToWholeLims)
    {
        if (records.Count == 0)
            return null;

        int today = records.Max(r => r.Day);
        var window = records.Where(r => r.Day > today - DemandTuner.WindowDays).ToList();
        if (window.Count == 0)
            return null;

        // Row 1: capacity-bound (last day's sold at/over platable)
        var lastDayRec = window.OrderByDescending(r => r.Day).First();
        if (lastDayRec.Sold >= platableToday && platableToday > 0)
            return null;

        // Row 2: throughput-bound (walk-outs; capacity already excluded by row 1)
        int walkedOut = window.Sum(r => r.WalkedOut);
        if (walkedOut > 0)
            return null;

        // ---- MIN-VOLUME GATE: demand-clean trailing average (the DemandTuner's
        // logic: sold-out days censored, clean days averaged; all-sold-out -> max).
        // Below the gate: hold - both levers blocked (findings: Roaches, Sweet Waffle).
        double cleanAvg = CleanDemandAverage(window);
        if (cleanAvg < minVolumeGate)
            return null;

        int floor = costHundredths + floorMarginHundredths;
        double avgSold = window.Average(r => r.Sold);

        // ---- Down-lever: sticky-down crawl ----
        var ordered = window.OrderByDescending(r => r.Day).ToList();
        var lastDay = ordered[0];
        var priorDay = ordered.Count > 1 ? ordered[1] : null;
        bool slumping = SlumpToday(window, slumpThresholdPct)
                        && priorDay != null
                        && SlumpsBelow(window, priorDay.Sold, slumpThresholdPct);
        if (slumping && currentPrice > floor)
        {
            int rawCut = currentPrice - currentPrice * crawlStepBps / 10000;
            int cut = Math.Max(floor, rawCut);
            cut = roundToWholeLims ? FloorToLims(cut) : cut;
            if (cut >= currentPrice) return null;   // quantization or floor erased the cut: hold
            return new PriceSuggestion(dish.Id, currentPrice, cut,
                "demand-bound: slump",
                $"{(int)cleanAvg:0.#}-day trailing demand below trend for {stickyDownTriggerDays} days; trimming toward the floor, not below it");
        }

        // ---- Up-lever: phase-aware district anchoring ----
        if (districtMedian is int median)
        {
            int anchor = established ? median : median * districtFloorBps / 10000;
            anchor = roundToWholeLims ? RoundToLims(anchor) : anchor;

            int gapToAnchor = Math.Abs(anchor - currentPrice);
            bool atMarket = gapToAnchor * 100 <= anchor * anchorAtMarketPct;
            if (atMarket)
            {
                int step = currentPrice + currentPrice * crawlStepBps / 10000;
                int capped = Math.Min(step, dish.PriceCapHundredths);
                if (roundToWholeLims) capped = CeilingToLims(capped);
                if (capped <= currentPrice) return null;
                return new PriceSuggestion(dish.Id, currentPrice, capped,
                    established ? "demand-bound: crawl up (established)" : "demand-bound: crawl up (entry)",
                    $"at market ({currentPrice / 100m:0.00} vs anchor {anchor / 100m:0.00}); verified +5% step toward ceiling");
            }

            if (currentPrice < anchor)
                return new PriceSuggestion(dish.Id, currentPrice, anchor,
                    established ? "demand-bound: catch up to district median" : "entry: open at district floor",
                    established
                        ? $"district median {median / 100m:0.00}; you charge {currentPrice / 100m:0.00} - proven market rate, then crawl"
                        : $"entry venue: open at {anchor / 100m:0.00} (95% of median) to build the base, bump in kind later");

            return null;   // above anchor: the crawl's job, one step at a time
        }

        // No district benchmark: crawl on own data only
        int step2 = currentPrice + currentPrice * crawlStepBps / 10000;
        int capped2 = Math.Min(step2, dish.PriceCapHundredths);
        if (roundToWholeLims) capped2 = CeilingToLims(capped2);
        if (capped2 <= currentPrice) return null;
        return new PriceSuggestion(dish.Id, currentPrice, capped2,
            "demand-bound: crawl up (no district benchmark)",
            $"you are the market here; verified +5% step on own data, toward ceiling");
    }

    // Demand-clean trailing average (DemandTuner semantics, inline):
    // sold-out days censored (their Sold is a floor, not a level); clean days averaged;
    // all-sold-out -> max observed (still a floor - demand at least that).
    static double CleanDemandAverage(List<SaleRecord> window)
    {
        var clean = window.Where(r => !r.SoldOut).ToList();
        if (clean.Count > 0) return clean.Average(r => r.Sold);
        return window.Max(r => r.Sold);
    }

    // Direction-preserve quantization (operator ruling: player UI snaps to whole
    // lims; suggestions must never round against the doctrine's direction).
    static int CeilingToLims(int hundredths) => ((hundredths + 99) / 100) * 100;
    static int FloorToLims(int hundredths) => (hundredths / 100) * 100;
    static int RoundToLims(int hundredths) => ((hundredths + 50) / 100) * 100;

    static bool SlumpToday(List<SaleRecord> window, int thresholdPct)
    {
        var lastDay = window.OrderByDescending(r => r.Day).First();
        var earlier = window.Where(r => r.Day < lastDay.Day).ToList();
        if (earlier.Count == 0) return false;
        double earlierAvg = earlier.Average(r => r.Sold);
        return lastDay.Sold <= earlierAvg * (100 - thresholdPct) / 100.0;
    }

    static bool SlumpsBelow(List<SaleRecord> window, int priorSold, int thresholdPct)
    {
        var ordered = window.OrderBy(r => r.Day).ToList();
        var day1 = ordered.Count >= 3 ? ordered[^3] : ordered.FirstOrDefault();
        if (day1 == null) return false;
        return priorSold <= day1.Sold * (100 - thresholdPct) / 100.0;
    }
}