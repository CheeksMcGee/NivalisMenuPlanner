using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisMenuPlanner.Core;

/// <summary>
/// The pricing doctrine, transcribed from the operator's four rulings:
/// (Q2) capacity-first diagnosis — price moves are reserved for demand problems,
///      never applied to inventory or throughput problems;
/// (Q3, restauranteur's version) phase-aware district anchoring — entry venues
///      (low lifetime reputation) compete on price at the district floor;
///      established venues catch up to the district median, then crawl. Thin
///      district coverage means NO anchor — crawl on own data, never jump on
///      someone else's market;
/// (down-lever) sticky-down: -5% only on 2 consecutive slumping days with stable
///      daily popularity; floor = cost + minimum margin;
/// (steady state) probe-up crawl: +5%/day steps toward the ceiling, each verified
///      against next-day sales; revert on demand shock. Within ~10% of the anchor
///      is "at market" — the crawl is the standing rule for everyone.
/// All advisory: suggestions, never writes. The manager-progression principle.
/// </summary>
public sealed record PriceSuggestion(
    string DishId, int CurrentPriceHundredths, int SuggestedPriceHundredths,
    string Diagnosis, string Rationale);

public static class PricingController
{
    // Doctrine constants (config-exposed in 5c; fixed here for the Core's purity)
    public const int CrawlStepBps = 500;             // +5% in basis-hundredths
    public const int StickyDownTriggerDays = 2;      // consecutive slump days
    public const int SlumpThresholdPct = 20;         // sales >=20% below trailing avg
    public const int FloorMarginHundredths = 200;    // cost + 2 lims
    public const int AnchorAtMarketPct = 10;         // within 10% of anchor = at market
    public const int DistrictFloorBps = 9500;        // entry anchor: 95% of district median

    /// <summary>
    /// One dish's suggestion, given everything the doctrine knows. Pure: same
    /// inputs, same suggestion, no state — the caller (5c) threads day history.
    /// Returns null when no move is warranted (the common case; holding is a
    /// decision, not a failure).
    /// </summary>
    /// <param name="dish">The dish (cap = the city/district measured or CSV cap).</param>
    /// <param name="currentPrice">What the venue charges now, hundredths.</param>
    /// <param name="records">The dish's SaleRecord history at this venue (the
    ///        DemandTuner's window source; last 3 days used here).</param>
    /// <param name="costHundredths">Effective per-plate cost (planner's EffCost).</param>
    /// <param name="platableToday">Servings the pantry could plate (capacity guard).</param>
    /// <param name="districtMedian">District median observed price, or null when
    ///        coverage is thin (the Boat Hangar case — you ARE the market).</param>
    /// <param name="established">True when the venue's lifetime reputation reads
    ///        "established" (phase gate; 5c derives it from popularity/rating).</param>
    public static PriceSuggestion? Suggest(
        DishInfo dish, int currentPrice, IReadOnlyList<SaleRecord> records,
        int costHundredths, int platableToday, int? districtMedian, bool established)
    {
        // ---- Q2: the diagnosis ladder. Price never touches rows 1 and 2. ----
        if (records.Count == 0)
            return null;                                    // cold start: hold, let the menu decide

        int today = records.Max(r => r.Day);
        var window = records.Where(r => r.Day > today - DemandTuner.WindowDays).ToList();
        if (window.Count == 0)
            return null;

        int sold = window.Sum(r => r.Sold);
        int cooked = window.Sum(r => r.Cooked);
        int platable = platableToday;
        int floor = costHundredths + FloorMarginHundredths;

        // Row 1: capacity-bound — YESTERDAY's demand was supply-capped (last day's
        // sold at/over what the pantry could plate). Window-sum vs single-day
        // platable was an apples/oranges overtrigger (my fixture trace caught it:
        // 3 days of healthy sales read as "capacity-bound" against one day's platable).
        var lastDayRec = window.OrderByDescending(r => r.Day).First();
        if (lastDayRec.Sold >= platable && platable > 0)
            return null;

        // Row 2: throughput-bound — plates died at the dishwasher (the walk-out domain).
        // Row 1 already ruled out capacity (we only reach here when last-day sold < platable),
        // so the platable check here was redundant AND carried the sum-vs-day bug (window-sum
        // sold vs single-day platable — the same shape I fixed in row 1 but missed here:
        // T19b's window-sum 63 vs platable 50 made row 2 unreachable). Any abandoned plates,
        // once capacity is excluded, are service-domain: price holds, staffing advice fires.
        int walkedOut = window.Sum(r => r.WalkedOut);
        if (walkedOut > 0)
            return null;   // service problem; staffing advice fires in v0.2.x, price holds

        // Row 3: demand-bound. Price is now the only lever left.
        double avgSold = window.Average(r => r.Sold);

        // ---- Down-lever: sticky-down crawl (2-day slump, stable popularity) ----
        var lastDay = window.OrderByDescending(r => r.Day).First();
        var priorDay = window.OrderByDescending(r => r.Day).Skip(1).FirstOrDefault();
        bool slumping = SlumpToday(window) && priorDay != null && SlumpsBelow(window, priorDay.Sold);
        // NOTE: popularity veto is 5c's wiring (daily-pop drop blocks raises); Core takes
        // established-only inputs. Sticky-down's own gate is the 2-day trigger:
        if (slumping && currentPrice > floor)
        {
            int cut = Math.Max(floor, currentPrice - currentPrice * CrawlStepBps / 10000);
            return new PriceSuggestion(dish.Id, currentPrice, cut,
                "demand-bound: slump",
                $"2-day sales slump ({(int)avgSold}/day vs earlier); popularity stable - trimming toward the floor, not below it");
        }

        // ---- Up-lever: phase-aware anchoring (Q3, restauranteur's doctrine) ----
        if (districtMedian is int median)
        {
            int anchor = established ? median : median * DistrictFloorBps / 10000;

            // At market (within 10% of anchor): crawl only, toward the ceiling
            int gapToAnchor = Math.Abs(anchor - currentPrice);
            bool atMarket = gapToAnchor * 100 <= anchor * AnchorAtMarketPct;
            if (atMarket)
            {
                int step = currentPrice + currentPrice * CrawlStepBps / 10000;
                int capped = Math.Min(step, dish.PriceCapHundredths);
                if (capped <= currentPrice) return null;   // already at ceiling
                return new PriceSuggestion(dish.Id, currentPrice, capped,
                    established ? "demand-bound: crawl up (established)" : "demand-bound: crawl up (entry)",
                    $"at market ({currentPrice / 100m:0.00} vs anchor {anchor / 100m:0.00}); verified +5% step toward ceiling");
            }

            // Below market: catch up (established) or open low (entry)
            if (currentPrice < anchor)
                return new PriceSuggestion(dish.Id, currentPrice, anchor,
                    established ? "demand-bound: catch up to district median" : "entry: open at district floor",
                    established
                        ? $"district median {median / 100m:0.00}; you charge {currentPrice / 100m:0.00} - proven market rate, then crawl"
                        : $"entry venue: open at {anchor / 100m:0.00} (95% of median) to build the base, bump in kind later");

            // Above anchor (price > median): hold — above-market pricing is the crawl's
            // job one verified step at a time, and you're already above the anchor
            return null;
        }

        // ---- No district benchmark (thin coverage — the Boat Hangar case): crawl only ----
        int step2 = currentPrice + currentPrice * CrawlStepBps / 10000;
        int capped2 = Math.Min(step2, dish.PriceCapHundredths);
        if (capped2 <= currentPrice) return null;
        return new PriceSuggestion(dish.Id, currentPrice, capped2,
            "demand-bound: crawl up (no district benchmark)",
            $"you are the market here; verified +5% step on own data, toward ceiling");
    }

    static bool SlumpToday(List<SaleRecord> window)
    {
        // today's sold >= SlumpThresholdPct below the window's earlier average
        var lastDay = window.OrderByDescending(r => r.Day).First();
        var earlier = window.Where(r => r.Day < lastDay.Day).ToList();
        if (earlier.Count == 0) return false;
        double earlierAvg = earlier.Average(r => r.Sold);
        return lastDay.Sold <= earlierAvg * (100 - SlumpThresholdPct) / 100.0;
    }

    static bool SlumpsBelow(List<SaleRecord> window, int priorSold)
    {
        // the prior day was ALSO at least SlumpThresholdPct below its own earlier average
        var ordered = window.OrderBy(r => r.Day).ToList();
        var day1 = ordered.Count >= 3 ? ordered[^3] : ordered.FirstOrDefault();
        if (day1 == null) return false;
        return priorSold <= day1.Sold * (100 - SlumpThresholdPct) / 100.0;
    }
}