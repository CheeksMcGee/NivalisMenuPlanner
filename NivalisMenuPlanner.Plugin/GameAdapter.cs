using System;
using System.Collections.Generic;
using System.Linq;
using Core = NivalisMenuPlanner.Core;
using Area = Nivalis.GhostSystem.CustomerLoop.VenueAreaGhost;
using NivalisModKit;

namespace NivalisMenuPlanner;

/// <summary>Bridges live kit queries into planner snapshots. Main thread only.</summary>
public static class GameAdapter
{
    /// <summary>
    /// Per-venue phase facts, computed at snapshot time from ReviewsOf and StaffOf.
    /// 5g-2: HasManager joins the record - the manager keystone ("can I leave it in a
    /// manager's hands?" asked directly; a hired manager implies level 3+, and a
    /// manager-less venue isn't delegable regardless of level). Computed here because
    /// area is in scope at snapshot time - the 5d-2 lesson.
    /// </summary>
    public sealed record PhaseFacts(bool HasManager, bool Established, float? RecentRating, int ReviewCount);

    public static List<string> KnownIds(Core.DishCatalog cat)
    {
        var known = new List<string>();
        foreach (var recipe in Recipes.Known)
        {
            var dish = Recipes.OutputOf(recipe);
            if (dish == null) continue;
            var id = Items.NameOf(dish);
            if (id != null && cat.ById(id) != null) known.Add(id);
        }
        return known.Distinct().ToList();
    }

    public static Core.PriceObservations HarvestPrices()
    {
        var obs = new Core.PriceObservations();
        int venues = 0;
        foreach (var area in Venues.All)
        {
            venues++;
            string? district = null;
            try
            {
                var loc = Venues.DistrictOf(area);
                district = loc == null ? null : World.NameOf(loc);
            }
            catch { }
            foreach (var entry in Venues.MenuOf(area))
            {
                var dishName = Items.NameOf(entry.Dish);
                if (dishName == null) continue;
                obs.Add(dishName, district ?? "?", entry.Price, Safe(() => Venues.NameOf(area)) ?? "?");
            }
        }
        Plugin.L.LogInfo($"Price sweep: {obs.ObservationCount} observations across {venues} venues.");
        return obs;
    }

    public static int? DistrictMedianOf(Core.PriceObservations? observations, string dishId, string district)
    {
        if (observations == null || string.IsNullOrEmpty(district)) return null;
        var range = observations.RangeIn(dishId, district);
        if (range is null) return null;
        int midpoint = (range.Value.min + range.Value.max) / 2;
        return midpoint;
    }

    /// <summary>
    /// The phase gate's reputation read: recent-window average and review count from
    /// ReviewsOf. (Post-Patch-#5: the count is the retained month, not lifetime - the
    /// streak gate replaced its role in established status.)
    /// </summary>
    static (float? recent, int count) ReadReputation(Area area)
    {
        int count = 0, recentCount = 0;
        double recentTotal = 0;
        float? recent = null;
        try
        {
            var reviews = Venues.ReviewsOf(area);
            int today = GameTime.Day;
            foreach (var r in reviews)
            {
                count++;
                if (r.Day > today - 7) { recentTotal += r.Score; recentCount++; }
            }
            if (recentCount > 0) recent = (float)(recentTotal / recentCount);
        }
        catch (Exception e) { Plugin.L.LogDebug($"ReviewsOf failed for phase: {e.Message}"); }
        return (recent, count);
    }

    /// <summary>
    /// The manager keystone (5g-2): does this venue have a hired manager? StaffOf
    /// exposes each member's roles as a string; the manager role check is the
    /// delegation test itself. Verified shape from the kit method dump.
    /// </summary>
    static bool HasManagerAt(Area area)
    {
        try
        {
            foreach (var s in Venues.StaffOf(area))
                if (s.Roles.Contains("Manager", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch (Exception e) { Plugin.L.LogDebug($"StaffOf failed for manager check: {e.Message}"); }
        return false;
    }

    public static IEnumerable<(string Key, Core.VenueState Venue, PhaseFacts Facts)> SnapshotAll(
        Core.DishCatalog cat, int lep, int maxSlots)
    {
        var known = KnownIds(cat);
        var priceCache = new Dictionary<string, int>();

        foreach (var area in Venues.PlayerOwned)
        {
            string key = Safe(() => Venues.NameOf(area)) ?? "?";
            string display = Safe(() => Venues.DisplayNameOf(area)) ?? key;
            string district;
            try
            {
                var loc = Venues.DistrictOf(area);
                district = loc == null ? "?" : (World.NameOf(loc) ?? "?");
            }
            catch { district = "?"; }

            var stock = new Dictionary<string, int>();
            var prices = new Dictionary<string, int>();
            foreach (var ing in cat.IngredientNames)
            {
                var item = Items.ByName(ing);
                if (item == null) continue;

                int have = TryInt(() => Venues.Stock(area, item));
                if (have > 0) stock[ing] = have;

                int best;
                if (priceCache.TryGetValue(ing, out var cached)) best = cached;
                else
                {
                    int min = int.MaxValue;
                    foreach (var vendor in Economy.VendorsFor(item))
                    {
                        var p = Economy.Price(vendor, item, default);
                        if (p != null && p > 0 && p < min) min = (int)p;
                    }
                    best = min == int.MaxValue ? 0 : min;
                    priceCache[ing] = best;
                }
                if (best > 0) prices[ing] = best;
            }

            // Phase facts: reputation read + manager keystone, both computed where
            // area is in scope (the 5d-2 lesson, applied before shipping this time).
            var (recent, count) = ReadReputation(area);
            bool hasManager = HasManagerAt(area);
            var facts = new PhaseFacts(hasManager, Established: false, recent, count);

            yield return (key, new Core.VenueState(key, display, district, maxSlots, lep, 0,
                stock, prices, cat.MixFor(district), known), facts);
        }
    }

    public static List<Core.StaffMember> AllStaff()
    {
        var all = new List<Core.StaffMember>();
        foreach (var area in Venues.PlayerOwned)
        {
            try
            {
                foreach (var s in Venues.StaffOf(area))
                {
                    all.Add(new Core.StaffMember(s.Name ?? "?", (decimal)s.Wage / 100m,
                        (int)Math.Round(s.HoursLeftToday * 60.0)));
                }
            }
            catch (Exception e) { Plugin.L.LogDebug($"StaffOf failed for a venue: {e.Message}"); }
        }
        return all;
    }

    static string? Safe(Func<string?> f) { try { return f(); } catch { return null; } }
    static int TryInt(Func<int> f) { try { return f(); } catch { return 0; } }
}