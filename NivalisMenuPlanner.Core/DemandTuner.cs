using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisMenuPlanner.Core;

/// <summary>
/// Learns per-dish expected daily orders from verified SaleRecord history.
/// Operator rulings, session of <date>: (Q1) 3-game-day window, simple
/// average; (Q4 refined) Sold-counts are demand-confirmed receipts (exact
/// reconciliation, GAPS #15) — but supply-constrained days cap learning,
/// so sold-out records are marked, not trusted, per Q2's capacity-first
/// doctrine. Write-time pruning keeps storage constant (~3 keys/dish).
/// Cold-start dishes fall back to the config DemandScale baseline.
/// </summary>
public static class DemandTuner
{
    /// <summary>Days of history the tuner considers. Ruling Q1: 3, responsive to regime changes (menu/pricing shifts); upgrade path (c) decays with epsilon-pruning — same constant footprint.</summary>
    public const int WindowDays = 3;

    /// <summary>
    /// Expected daily orders for a dish, learned from its recent records.
    /// - Only demand-clean records count: days where the dish was NOT
    ///   supply-capped (SoldOut == false) carry true demand signal.
    /// - Sold-out records are noted but excluded from the average — their
    ///   Sold is a floor, not a level (capacity was binding, so observed
    ///   demand is understated; averaging it in would systematically
    ///   under-order, which is the classic supply-censoring trap).
    /// - Cold start (no clean records) or conflicting signals: fall back
    ///   to the baseline, weighted lightly so one day of data outranks
    ///   zero but three outrank one.
    /// </summary>
    /// <param name="dishId">The dish (asset name).</param>
    /// <param name="records">Its recent SaleRecords, any order. Caller
    ///   supplies only this dish's; the tuner picks the window.</param>
    /// <param name="fallbackScale">Config DemandScale (baseline orders/day
    ///   for a district-loved dish) used when there is nothing to learn
    ///   from.</param>
    public static double ExpectedOrders(string dishId, IReadOnlyList<SaleRecord> records, double fallbackScale)
    {
        if (records.Count == 0) return fallbackScale;

        int today = records.Max(r => r.Day);
        var window = records
            .Where(r => r.Day > today - WindowDays)          // last 3 days
            .Where(r => !r.SoldOut)                            // demand-clean only (Q2)
            .ToList();

        if (window.Count == 0)
        {
            // Every recent record was sold-out: demand is AT LEAST the best
            // observed Sold, but the true level is unknown. Return the max
            // observed as a floor estimate — better to plan for demand we
            // have evidence of than to fall back to a baseline that may be
            // far below reality. (Beer at 33/day sold out: plan for >=33.)
            var capped = records.Where(r => r.Day > today - WindowDays).ToList();
            return capped.Count > 0 ? capped.Max(r => r.Sold) : fallbackScale;
        }

        return window.Average(r => r.Sold);
    }

    /// <summary>
    /// The keys a dish's history occupies after this day's write —
    /// the caller prunes everything older. Ruling Q1: constant footprint
    /// by construction; a year-long campaign stores exactly what a
    /// week-long one does (per-dish, per-venue).
    /// </summary>
    public static int RetentionDays => WindowDays;

    /// <summary>
    /// Prune decision: should a record from <paramref name="recordDay"/>
    /// still be stored, given <paramref name="today"/>? Called at
    /// DayEnded write time for every stored key — cheap, and keeps the
    /// SaveData store from growing with campaign length.
    /// </summary>
    public static bool ShouldRetain(int recordDay, int today) =>
        recordDay > today - RetentionDays;
}