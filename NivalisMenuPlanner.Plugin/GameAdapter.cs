using System;
using System.Collections.Generic;
using System.Linq;
using Core = NivalisMenuPlanner.Core;
using NivalisModKit;

namespace NivalisMenuPlanner;

/// <summary>Bridges live kit queries into planner snapshots. Main thread only (events/scheduler).</summary>
public static class GameAdapter
{
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

    /// <summary>
    /// NPC PRICE SWEEP: every menu in the city (Venues.All includes NPC venues),
    /// each entry's actual price, grouped by the venue's district. One pass at
    /// GameReady; menus are slow-changing, so no per-day refresh needed.
    /// </summary>
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
                obs.Add(dishName, district ?? "?", entry.Price);
            }
        }
        Plugin.L.LogInfo($"Price sweep: {obs.ObservationCount} observations across {venues} venues.");
        return obs;
    }

    public static IEnumerable<(string Key, Core.VenueState Venue)> SnapshotAll(
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

            yield return (key, new Core.VenueState(key, display, district, maxSlots, lep, 0,
                stock, prices, cat.MixFor(district), known));
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