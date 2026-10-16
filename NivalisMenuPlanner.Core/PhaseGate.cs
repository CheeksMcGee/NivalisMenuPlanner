using System;

namespace NivalisMenuPlanner.Core;

/// <summary>
/// The established-venue gate, redesigned after Patch #5 broke the review-count
/// semantics (ReviewsOf now serves a retained month, not lifetime). The operator's
/// definition: "established = a business with a reliable, repeatable profit model
/// that I could leave in a manager's hands without chaos."
///
/// Four prongs, all config-dialable:
///   1. Venue level >= PhaseMinVenueLevel (the game's own manager-hire gate:
///      level 3 = delegation-certified. The legitimacy prong.)
///   2. Consecutive profitable days >= PhaseConsecutiveDays (the streak; a
///      negative-profit day RESETS it - the model broke.)
///   3. Each profitable day clears a margin floor: profit >= PhaseMinProfitPct
///      of revenue ("a 1% day isn't a banger" - the quality prong.)
///   4. Recent rating >= PhaseRecentRating (freeze, not reset, on dips: one bad
///      service day pauses the streak, it doesn't restart it. The asymmetry
///      ruling: profit failures break the model; rating dips are noise.)
///
/// Self-healing across patches: all inputs are the mod's own harvest
/// (revenue reconciles to the game's screens to the lim) plus the game's
/// venue level - no API surface that upstream changes can silently break.
/// </summary>
public static class PhaseGate
{
    // ---- Ruled defaults (5g-2 makes these config dials) ----
    public const int DefaultMinVenueLevel = 3;
    public const int DefaultConsecutiveDays = 10;
    public const int DefaultMinProfitPct = 20;
    public const float DefaultRecentRating = 4.5f;

    /// <summary>One day's profit accounting, the streak's input.</summary>
    public readonly record struct DayResult(decimal Revenue, decimal Cost)
    {
        public decimal Profit => Revenue - Cost;
        /// <summary>Margin floor check: profit >= minPct% of revenue.</summary>
        public bool ClearsFloor(int minPct) =>
            Revenue > 0 && Profit * 100 >= Revenue * minPct;
    }

    /// <summary>
    /// The streak state, updated one day at a time. Pure: the caller owns
    /// persistence (SaveData in the plugin); this is the transition function.
    /// </summary>
    public readonly record struct Streak(int Count, bool Frozen)
    {
        public static Streak Start => new(0, false);

        /// <summary>
        /// One day's transition. Profit floor failure -> RESET to 0.
        /// Rating dip below the threshold -> FREEZE (count holds, no increment).
        /// Profitable + rating OK -> increment.
        /// Frozen is cleared by any profitable, well-rated day; a frozen day
        /// that also fails profit resets (the model broke while paused).
        /// </summary>
        public Streak Next(DayResult day, bool ratingDipped)
        {
            if (!day.ClearsFloor(minPct: DefaultMinProfitPct))
                return Streak.Start;                       // loss day: reset
            if (ratingDipped)
                return this with { Frozen = true };        // rating dip: freeze, hold count
            return new Streak(Count + 1, Frozen: false);   // good day: increment, unfreeze
        }

        public bool MeetsDays(int required) => Count >= required;
    }

    /// <summary>
    /// The complete gate. The manager keystone (5g-2 reformulation): a hired manager
    /// IS the delegation test - it implies level 3+ (the game's gate) and means the
    /// venue actually runs on delegation, not just that it could. PhaseRequiresManager
    /// (config) can loosen the keystone for operators who want it off.
    /// </summary>
    public static bool IsEstablished(bool hasManager, Streak streak, float recentRating,
        bool requireManager = true,
        int consecutiveDays = DefaultConsecutiveDays,
        float recentThreshold = DefaultRecentRating) =>
        (!requireManager || hasManager)
        && streak.MeetsDays(consecutiveDays)
        && recentRating >= recentThreshold;
}