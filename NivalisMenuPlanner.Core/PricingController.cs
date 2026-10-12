using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisMenuPlanner.Core;

public sealed record PriceSuggestion(
    string DishId, int CurrentPriceHundredths, int SuggestedPriceHundredths,
    string Diagnosis, string Rationale);

public static class PricingController
{
    // ---- Doctrine defaults (config-fed in 5d-2; Core takes them as parameters
    // so the plugin can dial whatever the operator configures) ----
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
    /// Q2 ladder: capacity -> throughput -> demand-bound (price is the last
    /// lever, never the first). 5d additions per session findings:
    ///  - MIN-VOLUME GATE (both levers): dishes averaging under MinVolumeGate
    ///    clean-demand get no price moves at all - dying dishes are the
    ///    profit bar's jurisdiction (Roaches/Waffle findings).
    ///  - WHOLE-LIM QUANTIZATION, direction-preserve, round-then-cap: round
    ///    to the step first, clamp to the ceiling last, so quantization can
    ///    never pierce the cap (T21d's catch). Crawls ceil (never stall at
    ///    >= 1 lim); cuts floor (never shallow the cut). RoundToWholeLims =
    ///    false preserves hundredths for the manager-era path.
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

        // Row 2: throughput-bound (walk-outs; capacity already excluded)
        int walkedOut = window.Sum(r => r.WalkedOut);
        if (walkedOut > 0)
            return null;

        // ---- MIN-VOLUME GATE ----
        double cleanAvg = CleanDemandAverage(window);
        if (cleanAvg < minVolumeGate)
            return null;

        int floor = costHundredths + floorMarginHundredths;

        // ---- Down-lever: sticky-down crawl ----
        var ordered = window.OrderByDescending(r => r.Day).ToList();
        var priorDay = ordered.Count > 1 ? ordered[1] : null;
        bool slumping = SlumpToday(window, slumpThresholdPct)
                        && priorDay != null
                        && SlumpsBelow(window, priorDay.Sold, slumpThresholdPct);
        if (slumping && currentPrice > floor)
        {
            int rawCut = currentPrice - currentPrice * crawlStepBps / 10000;
            int cut = Math.Max(floor, rawCut);
            cut = roundToWholeLims ? FloorToLims(cut) : cut;
            if (cut >= currentPrice) return null;   // quantization/floor erased the cut: hold
            if (cut < floor) cut = floor;         // never round below the floor
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
                // round first, cap last (T21d lesson: cap-then-round can pierce the ceiling)
                int step = currentPrice + currentPrice * crawlStepBps / 10000;
                if (roundToWholeLims) step = CeilingToLims(step);
                int capped = Math.Min(step, dish.PriceCapHundredths);
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

            return null;   // above anchor: crawl's job, one step at a time
        }

        // No district benchmark: crawl on own data only
        // round first, cap last (T21d: min(400, 350) = 350, not ceil-then-400)
        int step2 = currentPrice + currentPrice * crawlStepBps / 10000;
        if (roundToWholeLims) step2 = CeilingToLims(step2);
        int capped2 = Math.Min(step2, dish.PriceCapHundredths);
        if (capped2 <= currentPrice) return null;
        return new PriceSuggestion(dish.Id, currentPrice, capped2,
            "demand-bound: crawl up (no district benchmark)",
            $"you are the market here; verified +5% step on own data, toward ceiling");
    }

    static double CleanDemandAverage(List<SaleRecord> window)
    {
        var clean = window.Where(r => !r.SoldOut).ToList();
        if (clean.Count > 0) return clean.Average(r => r.Sold);
        return window.Max(r => r.Sold);   // all sold-out: max observed (a floor)
    }

    // Direction-preserve quantization. Round-then-cap: the ceiling clamps
    // AFTER rounding so quantization can never push a suggestion past the cap.
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