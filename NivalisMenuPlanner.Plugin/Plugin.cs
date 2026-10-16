using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using NivalisMenuPlanner.Core;
using NivalisModKit;
using UnityEngine.InputSystem;

namespace NivalisMenuPlanner;

[BepInPlugin(Guid, "Nivalis Menu Planner", "0.1.0")]
[BepInDependency(ModKit.Guid, ">=0.6.2")]
public class Plugin : BasePlugin
{
    public const string Guid = "CheeksMcGee.nivalis.menuplanner";

    internal static ManualLogSource L = null!;
    internal static ModSaveData Data = null!;
    static DishCatalog? _catalog;
    static PriceObservations? _observations;
    static readonly List<Rotation> _rotation = new();
    static readonly Dictionary<string, Dictionary<string, int>> _cookedByVenue =
        new(StringComparer.OrdinalIgnoreCase);

    internal static ConfigEntry<Key> Hotkey = null!;
    internal static ConfigEntry<int> MaxSlots = null!, MaxPerType = null!;
    internal static ConfigEntry<bool> PreferBeverage = null!, PreferEntree = null!, PreferDessert = null!;
    internal static ConfigEntry<float> ChurnPenalty = null!, StockDiscount = null!, DemandScale = null!,
                                      RoleBonus = null!, NotifyGain = null!, RaiseBuffer = null!;
    internal static ConfigEntry<int> DefaultLocalExclusivity = null!;

    // 5d-2 Step 1: doctrine dials
    internal static ConfigEntry<int> CrawlStepBps = null!, StickyDownDays = null!, SlumpThresholdPct = null!,
        FloorMarginHundredths = null!, MinVolumeGate = null!;
    internal static ConfigEntry<bool> RoundToWholeLims = null!;

    // Phase gate — 5g-2: the redesigned dials
    internal static ConfigEntry<float> PhaseRecentRating = null!, RaiseVetoDelta = null!;
    internal static ConfigEntry<int> PhaseMinProfitPct = null!, PhaseConsecutiveDays = null!;
    internal static ConfigEntry<bool> PhaseRequiresManager = null!;
    // Retired (kept bound for config-file compatibility; the gate no longer reads it):
    internal static ConfigEntry<int> PhaseReviewCount = null!;

    // 5f-3: entry-posture menu concentration
    internal static ConfigEntry<int> EntryPostureMaxSlots = null!;

    static readonly Dictionary<string, float> _lastRecent = new(StringComparer.OrdinalIgnoreCase);

    static int _pollGen;
    static int _inputMode;
    static UnityEngine.KeyCode _legacyKey = UnityEngine.KeyCode.None;
    static bool _pollWarned;

    public override void Load()
    {
        L = Log;
        Data = SaveData.For(Guid);

        Hotkey = Config.Bind("General", "Hotkey", Key.F8, "Opens/closes the planner window in gameplay.");
        MaxSlots = Config.Bind("Menu", "MaxSlots", 10, new ConfigDescription("Menu slots to plan per venue.", new AcceptableValueRange<int>(1, 12)));
        MaxPerType = Config.Bind("Menu", "MaxPerType", 3, new ConfigDescription("Variety cap per dish type.", new AcceptableValueRange<int>(1, 10)));
        PreferBeverage = Config.Bind("Menu", "PreferBeverage", true, "Prefer at least one beverage per menu (preference — the profit bar outranks it).");
        PreferEntree = Config.Bind("Menu", "PreferEntree", true, "Prefer at least one entree per menu (preference — the profit bar outranks it).");
        PreferDessert = Config.Bind("Menu", "PreferDessert", true, "Prefer a dessert when it earns its slot (preference — the profit bar outranks it).");
        RoleBonus = Config.Bind("Menu", "RoleBonus", 0.10f, new ConfigDescription("Score lift for a role-covering dish — tips close calls only.", new AcceptableValueRange<float>(0f, 0.5f)));
        ChurnPenalty = Config.Bind("Rotation", "ChurnPenalty", 0.15f, new ConfigDescription("Profit haircut for dishes not on yesterday's menu.", new AcceptableValueRange<float>(0f, 0.5f)));
        StockDiscount = Config.Bind("Rotation", "StockDiscount", 0.2f, new ConfigDescription("Pantry stock counts at this fraction of market price.", new AcceptableValueRange<float>(0f, 1f)));
        DemandScale = Config.Bind("Rotation", "DemandScale", 8f, new ConfigDescription("Baseline orders/day (v0.2 self-tunes from sales).", new AcceptableValueRange<float>(0.5f, 50f)));
        NotifyGain = Config.Bind("Rotation", "NotifyGain", 5f, new ConfigDescription("Notify when the new plan beats the stored one by this %.", new AcceptableValueRange<float>(0f, 100f), new ModSetting { IsAdvanced = true }));
        RaiseBuffer = Config.Bind("Payroll", "RaiseBuffer", 0.10f, new ConfigDescription("Escrow margin over polled wages.", new AcceptableValueRange<float>(0f, 0.5f)));
        DefaultLocalExclusivity = Config.Bind("Planning", "LocalExclusivity", 3, new ConfigDescription("Local Exclusivity Preference (1-5).", new AcceptableValueRange<int>(1, 5), new ModSetting { IsAdvanced = true }));

        CrawlStepBps = Config.Bind("Pricing", "CrawlStepBps", 500, new ConfigDescription("Crawl step in basis points (500 = +5%/day).", new AcceptableValueRange<int>(100, 2000), new ModSetting { IsAdvanced = true }));
        StickyDownDays = Config.Bind("Pricing", "StickyDownDays", 2, new ConfigDescription("Consecutive slumping days before a cut fires.", new AcceptableValueRange<int>(1, 5), new ModSetting { IsAdvanced = true }));
        SlumpThresholdPct = Config.Bind("Pricing", "SlumpThresholdPct", 20, new ConfigDescription("% below trailing context that counts as a slump day.", new AcceptableValueRange<int>(5, 50), new ModSetting { IsAdvanced = true }));
        FloorMarginHundredths = Config.Bind("Pricing", "FloorMarginHundredths", 200, new ConfigDescription("Price never cuts below cost + this (hundredths).", new AcceptableValueRange<int>(0, 1000), new ModSetting { IsAdvanced = true }));
        MinVolumeGate = Config.Bind("Pricing", "MinVolumeGate", 3, new ConfigDescription("Dishes averaging fewer clean sales/day get NO price moves.", new AcceptableValueRange<int>(0, 20), new ModSetting { IsAdvanced = true }));
        RoundToWholeLims = Config.Bind("Pricing", "RoundToWholeLims", true, "Snap suggestions to whole lims (the edit UI only accepts whole lims).");

        PhaseRecentRating = Config.Bind("Pricing", "PhaseRecentRating", 4.5f, new ConfigDescription("Recent rating at/above which a venue counts as established (the rating prong).", new AcceptableValueRange<float>(1f, 5f), new ModSetting { IsAdvanced = true }));
        RaiseVetoDelta = Config.Bind("Pricing", "RaiseVetoDelta", 0.3f, new ConfigDescription("Day-over-day recent-rating drop that vetoes price RAISES for a day.", new AcceptableValueRange<float>(0f, 2f), new ModSetting { IsAdvanced = true }));
        PhaseMinProfitPct = Config.Bind("Pricing", "PhaseMinProfitPct", 20, new ConfigDescription("Daily profit must reach this % of revenue to count as a profitable day (the margin floor: a 1% day isn't a banger).", new AcceptableValueRange<int>(1, 100), new ModSetting { IsAdvanced = true }));
        PhaseConsecutiveDays = Config.Bind("Pricing", "PhaseConsecutiveDays", 10, new ConfigDescription("Consecutive profitable days required for established status (a loss day resets the counter).", new AcceptableValueRange<int>(1, 60), new ModSetting { IsAdvanced = true }));
        PhaseRequiresManager = Config.Bind("Pricing", "PhaseRequiresManager", true, new ConfigDescription("Require a hired manager for established status (the delegation keystone: a manager implies level 3+, and a manager-less venue isn't delegable regardless of level).", null, new ModSetting { IsAdvanced = true }));
        PhaseReviewCount = Config.Bind("Pricing", "PhaseReviewCount", 500, new ConfigDescription("[RETIRED - Patch #5 broke review semantics; the gate now uses the profit streak. Kept for config compatibility.]", new AcceptableValueRange<int>(0, 10000), new ModSetting { IsAdvanced = true }));

        EntryPostureMaxSlots = Config.Bind("Pricing", "EntryPostureMaxSlots", 4, new ConfigDescription("Menu slot cap for entry-posture venues (cold start: focused menu, smaller seed order).", new AcceptableValueRange<int>(1, 12), new ModSetting { IsAdvanced = true }));

        ModMenu.ListSettings(Guid);
        ModMenu.AddPage(Guid, "Menu Planner", w => PlannerWindow.BuildPage(w, Current()));

        GameEvents.GameReady += OnGameReady;
        GameEvents.GameEnded += OnGameEnded;
        GameEvents.DayStarted += _ => Scheduler.NextFrame(ReplanAll);
        GameEvents.DishCooked += OnDishCooked;
        GameEvents.DayEnded += OnDayEnded;

        DevCommands.Register(Guid, "plan", "venue=N: menu suggestions as JSON", PlanCommand);
        DevCommands.Register(Guid, "sales", "venue=N&day=N: stored SaleRecords as JSON", SalesCommand);
        DevCommands.Register(Guid, "prices", "dish=N: observed city prices as JSON", PricesCommand);
        DevCommands.Register(Guid, "suggest", "venue=N: pricing suggestions as JSON", SuggestCommand);
        DevCommands.Register(Guid, "venueinfo", "venue=N: venue facts (reviews, ratings, streak) as JSON", VenueInfoCommand);
        DevCommands.Register(Guid, "seedorder", "venue=N: the cold-start seed order for an unplanned pantry, as JSON", SeedOrderCommand);
        DevCommands.Register(Guid, "staffinfo", "venue=N: per-staff facts (wage, band, satisfaction, speed, shift, skills) as JSON", StaffInfoCommand);

        L.LogInfo($"Menu Planner 0.1.0 loaded — hotkey {Hotkey.Value} (scheduler-polled), kit {ModKit.Version}.");
    }

    static void OnGameReady()
    {
        StartHotkeyPoll();
        try
        {
            L.LogInfo($"Game ready — build: {GameBuild.Describe()}");
            var csv = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "dishdata.csv");
            _catalog = DishCatalog.LoadCsv(csv);
            L.LogInfo($"Catalog loaded: {_catalog.Count} dishes.");
            _observations = GameAdapter.HarvestPrices();
            var (overridden, kept) = _catalog!.OverrideCaps(_observations);
            L.LogInfo($"Real caps: {overridden} measured, {kept} estimated (CSV fallback).");
            ReplanAll();
        }
        catch (Exception e) { L.LogError($"Startup failed: {e}"); _catalog = null; }
    }

    static void OnGameEnded()
    {
        PlannerWindow.DestroyWindow();
        _rotation.Clear();
        _cookedByVenue.Clear();
        _lastRecent.Clear();
        _inputMode = 0;
    }

    static void OnDishCooked(DishCookedArgs a)
    {
        try
        {
            if (a?.Recipe == null || a.Area == null) return;
            string? venueKey = Safe(() => Venues.NameOf(a.Area));
            if (venueKey == null) return;
            var dish = Recipes.OutputOf(a.Recipe);
            var id = dish == null ? null : Items.NameOf(dish);
            if (id != null)
            {
                var perVenue = _cookedByVenue.GetValueOrDefault(venueKey)
                    ?? (_cookedByVenue[venueKey] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
                perVenue[id] = perVenue.GetValueOrDefault(id) + 1;
            }
        }
        catch { }
    }

    static void OnDayEnded(DayEndedArgs a)
    {
        try
        {
            int day = a.Day;
            int venues = 0, records = 0;
            var revenueByVenue = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var area in Venues.PlayerOwned)
            {
                string key = Safe(() => Venues.NameOf(area)) ?? "?";
                venues++;
                var menu = new List<(string dish, int price)>();
                foreach (var entry in Venues.MenuOf(area))
                {
                    var dishName = Items.NameOf(entry.Dish);
                    if (dishName == null) continue;
                    menu.Add((dishName, entry.Price));
                }
                var soldByDish = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var receipt in Venues.ReceiptsOf(area, day))
                {
                    if (receipt.Type != "Restaurant") continue;
                    var dishName = receipt.Dish == null ? null : Items.NameOf(receipt.Dish);
                    if (dishName != null && receipt.Amount > 0)
                        soldByDish[dishName] = soldByDish.GetValueOrDefault(dishName) + receipt.Count;
                }
                var cookedForVenue = _cookedByVenue.GetValueOrDefault(key);

                decimal venueRevenue = 0m;
                foreach (var (dish, price) in menu)
                {
                    int sold = soldByDish.GetValueOrDefault(dish);
                    int cooked = cookedForVenue?.GetValueOrDefault(dish) ?? 0;
                    if (sold == 0 && cooked == 0) continue;
                    var walked = Math.Max(0, cooked - sold);
                    var rec = new SaleRecord(day, price, sold, cooked, walked, null);
                    Data.Set($"sales.{key}.{dish}.{day}", rec);
                    records++;
                    venueRevenue += (decimal)sold * price / 100m;
                }
                revenueByVenue[key] = venueRevenue;
            }
            _cookedByVenue.Clear();

            // 5g-2: PROFIT TRACKER — the streak's daily input (approximate cost side)
            foreach (var kv in revenueByVenue)
            {
                string key = kv.Key;
                decimal revenue = kv.Value;

                decimal wages = 0m;
                try
                {
                    foreach (var area in Venues.PlayerOwned)
                    {
                        if (!string.Equals(Safe(() => Venues.NameOf(area)), key, StringComparison.OrdinalIgnoreCase))
                            continue;
                        foreach (var s in Venues.StaffOf(area))
                            wages += (decimal)s.Wage * (decimal)s.HoursLeftToday;
                    }
                }
                catch { }

                var shopping = Data.Get<List<ShoppingItem>>($"lastShopping.{key}", new List<ShoppingItem>());
                decimal spend = shopping.Sum(s => (decimal)s.Needed * s.UnitPriceHundredths) / 100m;

                var priorStreak = Data.Get<PhaseGate.Streak>($"profitStreak.{key}", PhaseGate.Streak.Start);
                float priorRecent = _lastRecent.GetValueOrDefault(key, 5f);
                bool ratingDipped = priorRecent < PhaseRecentRating.Value;
                var dayResult = new PhaseGate.DayResult(revenue, wages + spend);
                var newStreak = priorStreak.Next(dayResult, ratingDipped);
                Data.Set($"profitStreak.{key}", newStreak);
                L.LogInfo($"[streak] {key}: revenue {revenue:0.00} - est.cost {(wages + spend):0.00} = profit {(revenue - wages - spend):0.00} ({(revenue > 0 ? (int)((revenue - wages - spend) * 100 / revenue) : 0)}% of revenue); streak {priorStreak.Count} -> {newStreak.Count}{(newStreak.Frozen ? " (frozen)" : "")}");
            }

            foreach (var k in Data.Keys.Where(k => k.StartsWith("sales.", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                int dot = k.LastIndexOf('.');
                if (dot < 0) continue;
                if (int.TryParse(k[(dot + 1)..], out var recDay)
                    && !DemandTuner.ShouldRetain(recDay, day))
                    Data.Remove(k);
            }
            L.LogInfo($"Day {day} harvested: {records} sale records across {venues} venue(s); store pruned to the 3-day window.");
        }
        catch (Exception e) { L.LogWarning($"Sale harvest failed: {e.Message}"); }
    }

    static object SalesCommand(CommandArgs args)
    {
        string? venueFilter = args.Has("venue") ? args.Get("venue", "") : null;
        int? day = args.Has("day") ? args.GetInt("day", -1) : null;
        if (day == -1) day = null;
        var keys = Data.Keys.Where(k => k.StartsWith("sales.", StringComparison.OrdinalIgnoreCase));
        if (venueFilter != null)
            keys = keys.Where(k => k.Contains(venueFilter, StringComparison.OrdinalIgnoreCase));
        return keys.Select(k =>
        {
            var parts = k.Split('.');
            var rec = Data.Get<SaleRecord>(k, null!);
            return new { venue = parts[1], dish = parts[2], day = parts[3], record = rec };
        }).Where(x => day == null || x.day == day.Value.ToString())
          .OrderBy(x => x.day).ThenBy(x => x.venue).ThenBy(x => x.dish)
          .ToList();
    }

    static object PricesCommand(CommandArgs args)
    {
        if (_observations == null) return new { error = "price sweep not run yet" };
        string? dishFilter = args.Has("dish") ? args.Get("dish", "") : null;
        var dishes = _observations.ObservedDishes;
        if (dishFilter != null)
            dishes = dishes.Where(d => d.Contains(dishFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        return dishes.Select(d => new
        {
            dish = d,
            measuredCap = _observations.CapOf(d),
            observations = _observations.PricesOf(d).Count,
            priceRange = _observations.PricesOf(d).Min() != _observations.PricesOf(d).Max()
                ? $"{_observations.PricesOf(d).Min() / 100m:0.00}-{_observations.PricesOf(d).Max() / 100m:0.00}"
                : $"{_observations.PricesOf(d).Min() / 100m:0.00}"
        }).OrderBy(x => x.dish).ToList();
    }

    static object SuggestCommand(CommandArgs args)
    {
        var picks = Current();
        if (args.Has("venue"))
        {
            var frag = args.Get("venue", "");
            picks = picks.Where(r => r.Key.Contains(frag, StringComparison.OrdinalIgnoreCase)
                                  || r.Title.Contains(frag, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        return new
        {
            day = GameTime.Day,
            venues = picks.Select(r => new
            {
                venue = r.Title,
                established = r.Established,
                suggestions = r.Suggestions.Select(s => new
                {
                    dish = s.DishId,
                    diagnosis = s.Diagnosis,
                    rationale = s.Rationale,
                    current = s.CurrentPriceHundredths / 100m,
                    suggested = s.SuggestedPriceHundredths / 100m
                })
            })
        };
    }

    static object VenueInfoCommand(CommandArgs args)
    {
        var result = new List<object>();
        foreach (var area in Venues.PlayerOwned)
        {
            string key = Safe(() => Venues.NameOf(area)) ?? "?";
            if (args.Has("venue") && !key.Contains(args.Get("venue", ""), StringComparison.OrdinalIgnoreCase))
                continue;
            string display = Safe(() => Venues.DisplayNameOf(area)) ?? key;
            int count = 0, recentCount = 0, positive = 0;
            double total = 0, recentTotal = 0;
            double? lifetimeAvg = null, recentAvg = null; double positivePct = 0;
            try
            {
                var reviews = Venues.ReviewsOf(area);
                int today = GameTime.Day;
                foreach (var r in reviews)
                {
                    count++; total += r.Score;
                    if (r.Score >= 4.0) positive++;
                    if (r.Day > today - 7) { recentTotal += r.Score; recentCount++; }
                }
                if (count > 0) { lifetimeAvg = Math.Round(total / count, 2); positivePct = Math.Round(100.0 * positive / count, 0); }
                if (recentCount > 0) recentAvg = Math.Round(recentTotal / recentCount, 2);
            }
            catch (Exception e) { L.LogDebug($"ReviewsOf failed: {e.Message}"); }
            var streak = Data.Get<PhaseGate.Streak>($"profitStreak.{key}", PhaseGate.Streak.Start);
            bool hasManager = false;
            try
            {
                foreach (var s in Venues.StaffOf(area))
                    if (s.Roles.Contains("Manager", StringComparison.OrdinalIgnoreCase))
                    { hasManager = true; break; }
            }
            catch { }
            result.Add(new
            {
                venue = key,
                display,
                reviewCount = count,
                lifetimeAvg,
                recentAvg,
                positivePct,
                profitStreak = streak.Count,
                streakFrozen = streak.Frozen,
                hasManager
            });
        }
        return result;
    }

    // 5g-3: STAFFINFO — the discovery session's payoff. Per-staff facts via the kit's
    // StaffOf (which carries Person) -> Person.RuntimeData (the decoded model):
    // WorkSatisfaction (Happiness/WageInfluence/SkillInfluence/Value), Wage, Speed,
    // BaseWorkingHours vs WorkingHours (the shift-preference delta), Skills, and the
    // wage band via CalculateWageRange. Every read individually try-wrapped: an
    // IL2CPP edge case degrades to null rather than killing the pull.
    static object StaffInfoCommand(CommandArgs args)
    {
        var result = new List<object>();
        foreach (var area in Venues.PlayerOwned)
        {
            string key = Safe(() => Venues.NameOf(area)) ?? "?";
            if (args.Has("venue") && !key.Contains(args.Get("venue", ""), StringComparison.OrdinalIgnoreCase))
                continue;

            var staff = new List<object>();
            try
            {
                foreach (var s in Venues.StaffOf(area))
                {
                    object? satisfaction = null;
                    float? speed = null;
                    int? baseHours = null;
                    object? workingHours = null;
                    object? band = null;
                    object? skills = null;
                    int? wage = null;

                    try
                    {
                        var rd = s.Person?.RuntimeData;
                        if (rd != null)
                        {
                            try { wage = rd.Wage; } catch { }
                            try
                            {
                                var ws = rd.WorkSatisfaction;
                                satisfaction = new
                                {
                                    happiness = ws.Happiness.ToString(),
                                    wageInfluence = ws.WageInfluence,
                                    skillInfluence = ws.SkillInfluence,
                                    value = ws.Value
                                };
                            }
                            catch { }
                            try { speed = rd.Speed; } catch { }
                            try { baseHours = rd.BaseWorkingHours; } catch { }
                            try { workingHours = new { start = rd.WorkingHours.x, end = rd.WorkingHours.y }; } catch { }
                            try { band = rd.CalculateWageRange(0f); } catch { }
                            try
                            {
                                var skillList = new List<object>();
                                foreach (var kv in rd.Skills)
                                {
                                    try
                                    {
                                        skillList.Add(new
                                        {
                                            skill = kv.Key.ToString(),
                                            level = kv.Value
                                        });
                                    }
                                    catch { }
                                }
                                skills = skillList;
                            }
                            catch { }
                        }
                    }
                    catch { }

                    staff.Add(new
                    {
                        name = s.Name,
                        roles = s.Roles,
                        wage,
                        band,
                        satisfaction,
                        speed,
                        preferredShiftHours = baseHours,
                        scheduledShift = workingHours,
                        skills
                    });
                }
            }
            catch (Exception e) { L.LogDebug($"StaffOf failed for staffinfo: {e.Message}"); }

            result.Add(new { venue = key, staff });
        }
        return result;
    }

    static object SeedOrderCommand(CommandArgs args)
    {
        var picks = Current();
        if (args.Has("venue"))
        {
            var frag = args.Get("venue", "");
            picks = picks.Where(r => r.Key.Contains(frag, StringComparison.OrdinalIgnoreCase)
                                  || r.Title.Contains(frag, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        return new
        {
            day = GameTime.Day,
            venues = picks.Select(r => new
            {
                venue = r.Title,
                seedOrder = r.Suggestion.Warnings.Any(w => w.Contains("Cold start")),
                dishes = r.Suggestion.Dishes.Select(d => new { name = d.Dish.Name, orders = d.ExpectedOrders }),
                shopping = r.Suggestion.Shopping.Select(s => new
                {
                    item = s.Item,
                    needed = s.Needed,
                    unitPrice = s.UnitPriceHundredths / 100m,
                    protects = s.ProtectedHundredths / 100m,
                    cost = s.Needed * s.UnitPriceHundredths / 100m
                }),
                totalCost = r.Suggestion.Shopping.Sum(s => s.Needed * s.UnitPriceHundredths) / 100m
            })
        };
    }

    static void StartHotkeyPoll()
    {
        var gen = ++_pollGen;
        Scheduler.NextFrame(() => PollHotkey(gen));
    }

    static void PollHotkey(int gen)
    {
        if (gen != _pollGen) return;
        try
        {
            if (GameEvents.IsInGame && Hotkey != null)
            {
                if (_inputMode == 0) ProbeInputMode();
                bool pressed = false;
                if (_inputMode == 1)
                {
                    var kb = Keyboard.current;
                    var btn = kb == null ? null : kb[Hotkey.Value];
                    pressed = btn != null && btn.wasPressedThisFrame;
                }
                else if (_inputMode == 2)
                {
                    pressed = UnityEngine.Input.GetKeyDown(_legacyKey);
                }
                if (pressed) ToggleWindow();
            }
        }
        catch (Exception e)
        {
            if (!_pollWarned) { L.LogWarning($"Hotkey poll failed: {e}"); _pollWarned = true; }
        }
        Scheduler.NextFrame(() => PollHotkey(gen));
    }

    static void ProbeInputMode()
    {
        try { _inputMode = Keyboard.current != null ? 1 : 2; }
        catch { _inputMode = 2; }
        _legacyKey = MapKey(Hotkey.Value);
        L.LogInfo(_inputMode == 1
            ? $"Hotkey input mode: InputSystem — {Hotkey.Value}"
            : $"Hotkey input mode: legacy Input — {Hotkey.Value} -> {_legacyKey}");
    }

    static UnityEngine.KeyCode MapKey(Key k)
    {
        var n = k.ToString();
        if (n.StartsWith("Digit", StringComparison.OrdinalIgnoreCase)) n = "Alpha" + n[5..];
        else if (n.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase)) n = "Keypad" + n[6..];
        else if (n.Equals("Enter", StringComparison.OrdinalIgnoreCase)) n = "Return";
        else if (n.EndsWith("Ctrl", StringComparison.OrdinalIgnoreCase)) n = n[..^4] + "Control";
        return Enum.TryParse<UnityEngine.KeyCode>(n, out var c) ? c : UnityEngine.KeyCode.None;
    }

    internal static bool CatalogLoaded => _catalog != null;

    internal static MenuRules Rules() => new(
        MaxSlots.Value,
        PreferBeverage.Value, PreferEntree.Value, PreferDessert.Value,
        new[] { DishType.Meat, DishType.Fish, DishType.Seafood, DishType.Sandwich, DishType.Pizza, DishType.Soup },
        MaxPerType.Value, ChurnPenalty.Value, StockDiscount.Value, RoleBonus.Value);

    internal static void ReplanAll()
    {
        if (_catalog == null || !GameEvents.IsInGame) return;

        _rotation.Clear();
        foreach (var (key, venue, facts) in GameAdapter.SnapshotAll(
                     _catalog!, DefaultLocalExclusivity.Value, MaxSlots.Value))
        {
            var yesterday = Data.Get<List<string>>($"menus.{key}", new List<string>());

            var streak = Data.Get<PhaseGate.Streak>($"profitStreak.{key}", PhaseGate.Streak.Start);

            // The veto stays: a day-over-day rating drop still suppresses raises
            // for a day, even for established venues (the fast-metric guard).
            float prior = _lastRecent.GetValueOrDefault(key, float.NaN);
            bool veto = !float.IsNaN(prior)
                        && facts.RecentRating.HasValue
                        && prior - facts.RecentRating.Value >= RaiseVetoDelta.Value;
            bool established = !veto && PhaseGate.IsEstablished(
                facts.HasManager, streak,
                facts.RecentRating ?? 0f,
                requireManager: PhaseRequiresManager.Value,
                consecutiveDays: PhaseConsecutiveDays.Value,
                recentThreshold: PhaseRecentRating.Value);
            _lastRecent[key] = facts.RecentRating ?? prior;

            L.LogInfo($"[phase] {key}: manager={facts.HasManager} streak={streak.Count}{(streak.Frozen ? " (frozen)" : "")} recent={facts.RecentRating?.ToString("0.0") ?? "n/a"} established={established} (veto={veto})");

            // Entry-posture menu concentration (5f-3) rides the same gate:
            var venueRules = established
                ? Rules()
                : Rules() with { MaxSlots = Math.Min(EntryPostureMaxSlots.Value, MaxSlots.Value) };
            var suggestion = Planner.Plan(_catalog!, venue, venueRules, new MenuState(yesterday, DemandScale.Value));

            var priceSuggestions = new List<PriceSuggestion>();
            foreach (var line in suggestion.Dishes)
            {
                var dish = line.Dish;
                var history = LoadHistory(key, dish.Id);
                var currentPrice = CurrentMenuPrice(key, dish.Id) ?? dish.PriceCapHundredths;
                var median = GameAdapter.DistrictMedianOf(_observations, dish.Id, venue.District);
                L.LogInfo($"[pricing] {key}/{dish.Id}: price={currentPrice} records=[{PricingController.DiagnosticWindow(history)}] platable={Planner.ServingsPossible(dish, venue, _catalog!)} median={median} gate={MinVolumeGate.Value}");
                var sug = PricingController.Suggest(
                    dish, currentPrice, history,
                    line.CostHundredths,
                    Planner.ServingsPossible(dish, venue, _catalog!),
                    median, established,
                    minVolumeGate: MinVolumeGate.Value,
                    crawlStepBps: CrawlStepBps.Value,
                    stickyDownTriggerDays: StickyDownDays.Value,
                    slumpThresholdPct: SlumpThresholdPct.Value,
                    floorMarginHundredths: FloorMarginHundredths.Value,
                    roundToWholeLims: RoundToWholeLims.Value);
                if (sug != null) priceSuggestions.Add(sug);
            }

            _rotation.Add(new Rotation(key, venue.Name, suggestion, priceSuggestions, established));

            // Persist the shopping list for tomorrow's profit tracking (cost estimate).
            Data.Set($"lastShopping.{key}", suggestion.Shopping.Select(s => s).ToList());

            decimal? priorProfit = Data.Has($"profit.{key}") ? Data.Get<decimal?>($"profit.{key}", null) : null;
            if (priorProfit is > 0 && suggestion.EstProfitPerDay > priorProfit * (1 + (decimal)NotifyGain.Value / 100m))
                Ui.Notify("Menu Planner",
                    $"{venue.Name}: today's plan is worth ~{suggestion.EstProfitPerDay:0.00} lims/day, {(suggestion.EstProfitPerDay / priorProfit - 1) * 100:0}% better than the stored plan.");

            Data.Set($"menus.{key}", suggestion.Dishes.Select(d => d.Dish.Id).ToList());
            Data.Set($"profit.{key}", suggestion.EstProfitPerDay);
        }
        L.LogInfo($"Planned {_rotation.Count} venue(s) for day {GameTime.Day}, hour {GameTime.Hour}.");
    }

    static List<SaleRecord> LoadHistory(string venueKey, string dishId)
    {
        var records = new List<SaleRecord>();
        foreach (var k in Data.Keys.Where(k => k.StartsWith($"sales.{venueKey}.{dishId}.", StringComparison.OrdinalIgnoreCase)))
        {
            var rec = Data.Get<SaleRecord>(k, null!);
            if (rec != null) records.Add(rec);
        }
        return records;
    }

    static int? CurrentMenuPrice(string venueKey, string dishId)
    {
        try
        {
            foreach (var area in Venues.PlayerOwned)
            {
                if (!string.Equals(Safe(() => Venues.NameOf(area)), venueKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var entry in Venues.MenuOf(area))
                {
                    var name = Items.NameOf(entry.Dish);
                    if (string.Equals(name, dishId, StringComparison.OrdinalIgnoreCase))
                        return entry.Price;
                }
            }
        }
        catch { }
        return null;
    }

    internal static Rotation[] Current() => _rotation.ToArray();

    internal static void ToggleWindow()
    {
        if (!GameEvents.IsInGame) return;
        if (!CatalogLoaded) { Ui.Notify("Menu Planner", "dishdata.csv not found next to the plugin DLL."); return; }
        PlannerWindow.Toggle(Current());
    }

    internal static string? PayrollSummary()
    {
        try
        {
            var staff = GameAdapter.AllStaff();
            if (staff.Count == 0) return null;
            decimal wallet = (Economy.PlayerMoney ?? 0) / 100m;
            decimal owed = Payroll.Obligation(staff);
            decimal spendable = Payroll.Spendable(wallet, staff, (decimal)RaiseBuffer.Value);
            return $"Wallet {wallet:0.00} lims - wages owed {owed:0.00} - spendable {spendable:0.00} (full shift escrowed).";
        }
        catch (Exception e) { L.LogWarning($"PayrollSummary: {e.Message}"); return null; }
    }

    static object PlanCommand(CommandArgs a)
    {
        var picks = Current();
        if (a.Has("venue"))
        {
            var frag = a.Get("venue", "");
            picks = picks.Where(r => r.Key.Contains(frag, StringComparison.OrdinalIgnoreCase)
                                  || r.Title.Contains(frag, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        return new
        {
            day = GameTime.Day,
            venues = picks.Select(r => new
            {
                venue = r.Title,
                profitPerDay = r.Suggestion.EstProfitPerDay,
                dishes = r.Suggestion.Dishes.Select(d => new { name = d.Dish.Name, margin = d.Margin, orders = d.ExpectedOrders }),
                warnings = r.Suggestion.Warnings
            })
        };
    }

    static string? Safe(Func<string?> f) { try { return f(); } catch { return null; } }

    public sealed record Rotation(string Key, string Title, MenuSuggestion Suggestion,
        IReadOnlyList<PriceSuggestion> Suggestions, bool Established);
}