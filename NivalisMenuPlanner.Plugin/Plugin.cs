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

    // ---- 5d-2: doctrine dials (config-open ruling) ----
    internal static ConfigEntry<int> CrawlStepBps = null!, StickyDownDays = null!, SlumpThresholdPct = null!,
        FloorMarginHundredths = null!, MinVolumeGate = null!;
    internal static ConfigEntry<bool> RoundToWholeLims = null!;

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
        MaxPerType = Config.Bind("Menu", "MaxPerType", 3, new ConfigDescription("Variety cap per dish type (relaxes before slots go empty).", new AcceptableValueRange<int>(1, 10)));
        PreferBeverage = Config.Bind("Menu", "PreferBeverage", true, "Prefer at least one beverage per menu (preference — the profit bar outranks it).");
        PreferEntree = Config.Bind("Menu", "PreferEntree", true, "Prefer at least one entree per menu (preference — the profit bar outranks it).");
        PreferDessert = Config.Bind("Menu", "PreferDessert", true, "Prefer a dessert when it earns its slot (preference — the profit bar outranks it).");
        RoleBonus = Config.Bind("Menu", "RoleBonus", 0.10f, new ConfigDescription("Score lift for a dish covering a preferred role — tips close calls only.", new AcceptableValueRange<float>(0f, 0.5f)));
        ChurnPenalty = Config.Bind("Rotation", "ChurnPenalty", 0.15f, new ConfigDescription("Profit haircut for dishes not on yesterday's menu; 0 flips freely.", new AcceptableValueRange<float>(0f, 0.5f)));
        StockDiscount = Config.Bind("Rotation", "StockDiscount", 0.2f, new ConfigDescription("Pantry stock counts at this fraction of market price (use it or lose it).", new AcceptableValueRange<float>(0f, 1f)));
        DemandScale = Config.Bind("Rotation", "DemandScale", 8f, new ConfigDescription("Baseline orders/day for a dish this district loves (v0.2 self-tunes from sales).", new AcceptableValueRange<float>(0.5f, 50f)));
        NotifyGain = Config.Bind("Rotation", "NotifyGain", 5f, new ConfigDescription("Notify at day start when the new plan beats the stored one by this %.", new AcceptableValueRange<float>(0f, 100f), new ModSetting { IsAdvanced = true }));
        RaiseBuffer = Config.Bind("Payroll", "RaiseBuffer", 0.10f, new ConfigDescription("Escrow margin over polled wages for mid-shift raise requests.", new AcceptableValueRange<float>(0f, 0.5f)));
        DefaultLocalExclusivity = Config.Bind("Planning", "LocalExclusivity", 3, new ConfigDescription("Local Exclusivity Preference (1-5) until the kit exposes it; 1-2 favors cheap dishes, 4-5 exclusive ones.", new AcceptableValueRange<int>(1, 5), new ModSetting { IsAdvanced = true }));

        // ---- Pricing doctrine dials (config-open ruling; defaults = session-calibrated) ----
        CrawlStepBps = Config.Bind("Pricing", "CrawlStepBps", 500, new ConfigDescription("Crawl step in basis points (500 = +5%/day).", new AcceptableValueRange<int>(100, 2000), new ModSetting { IsAdvanced = true }));
        StickyDownDays = Config.Bind("Pricing", "StickyDownDays", 2, new ConfigDescription("Consecutive slumping days before a price cut fires.", new AcceptableValueRange<int>(1, 5), new ModSetting { IsAdvanced = true }));
        SlumpThresholdPct = Config.Bind("Pricing", "SlumpThresholdPct", 20, new ConfigDescription("A day counts as slumping when sales are at least this % below its trailing context.", new AcceptableValueRange<int>(5, 50), new ModSetting { IsAdvanced = true }));
        FloorMarginHundredths = Config.Bind("Pricing", "FloorMarginHundredths", 200, new ConfigDescription("Price never cuts below cost + this (hundredths).", new AcceptableValueRange<int>(0, 1000), new ModSetting { IsAdvanced = true }));
        MinVolumeGate = Config.Bind("Pricing", "MinVolumeGate", 3, new ConfigDescription("Dishes averaging fewer clean sales/day than this get NO price moves (the Roaches/Waffle findings).", new AcceptableValueRange<int>(0, 20), new ModSetting { IsAdvanced = true }));
        RoundToWholeLims = Config.Bind("Pricing", "RoundToWholeLims", true, "Snap suggestions to whole lims (the edit UI only accepts whole lims).");

        ModMenu.ListSettings(Guid);
        ModMenu.AddPage(Guid, "Menu Planner", w => PlannerWindow.BuildPage(w, Current()));

        GameEvents.GameReady += OnGameReady;
        GameEvents.GameEnded += OnGameEnded;
        GameEvents.DayStarted += _ => Scheduler.NextFrame(ReplanAll);
        if (!GameEvents.IsAvailable(nameof(GameEvents.DayStarted)))
            L.LogWarning("DayStarted unavailable — day-start replanning disabled this session.");
        GameEvents.DishCooked += OnDishCooked;
        GameEvents.DayEnded += OnDayEnded;

        DevCommands.Register(Guid, "plan", "venue=N: current menu suggestions as JSON (N = name fragment; omit for all)", PlanCommand);
        DevCommands.Register(Guid, "sales", "venue=N&day=N: stored SaleRecords as JSON", SalesCommand);
        DevCommands.Register(Guid, "prices", "dish=N: observed city prices and measured caps as JSON", PricesCommand);
        DevCommands.Register(Guid, "suggest", "venue=N: current pricing suggestions as JSON", SuggestCommand);
        DevCommands.Register(Guid, "venueinfo", "venue=N: venue facts (reviews, ratings, storage) as JSON", VenueInfoCommand);

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
        catch { /* best-effort counting */ }
    }

    static void OnDayEnded(DayEndedArgs a)
    {
        try
        {
            int day = a.Day;
            int venues = 0, records = 0;
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

                foreach (var (dish, price) in menu)
                {
                    int sold = soldByDish.GetValueOrDefault(dish);
                    int cooked = cookedForVenue?.GetValueOrDefault(dish) ?? 0;
                    if (sold == 0 && cooked == 0) continue;
                    var walked = Math.Max(0, cooked - sold);
                    float? pop = null;   // popularity wiring: rides the venueinfo probe (GAPS #19)
                    var rec = new SaleRecord(day, price, sold, cooked, walked, pop);
                    Data.Set($"sales.{key}.{dish}.{day}", rec);
                    records++;
                }
            }
            _cookedByVenue.Clear();
            foreach (var k in Data.Keys.Where(k => k.StartsWith("sales.", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var parts = k.Split('.');
                if (parts.Length == 5 && int.TryParse(parts[4], out var recDay)
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
        if (_observations == null) return new { error = "price sweep not run yet (no GameReady this session)" };
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
            result.Add(new { venue = key, display, reviewCount = count, lifetimeAvg, recentAvg, positivePct });
        }
        return result;
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
            ? $"Hotkey input mode: InputSystem (Keyboard present) — {Hotkey.Value}"
            : $"Hotkey input mode: legacy Input (Keyboard.current null) — {Hotkey.Value} -> {_legacyKey}");
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
        foreach (var (key, venue) in GameAdapter.SnapshotAll(_catalog!, DefaultLocalExclusivity.Value, MaxSlots.Value))
        {
            var yesterday = Data.Get<List<string>>($"menus.{key}", new List<string>());
            var suggestion = Planner.Plan(_catalog!, venue, Rules(), new MenuState(yesterday, DemandScale.Value));

            // ---- pricing pass: config dials feed the doctrine; [pricing] logs its inputs ----
            var priceSuggestions = new List<PriceSuggestion>();
            foreach (var line in suggestion.Dishes)
            {
                var dish = line.Dish;
                var history = LoadHistory(key, dish.Id);
                var currentPrice = CurrentMenuPrice(key, dish.Id) ?? dish.PriceCapHundredths;
                var median = GameAdapter.DistrictMedianOf(_observations, dish.Id, venue.District);
                L.LogInfo($"[pricing] {key}/{dish.Id}: price={currentPrice} records=[{string.Join(",", history.Select(r => $"d{r.Day}:s{r.Sold}"))}] platable={Planner.ServingsPossible(dish, venue, _catalog!)} median={median} gate={MinVolumeGate.Value}");
                var sug = PricingController.Suggest(
                    dish, currentPrice, history,
                    line.CostHundredths,
                    Planner.ServingsPossible(dish, venue, _catalog!),
                    median,
                    established: false,   // Step 2 wires the phase gate (area plumbing); blind-entry-by-design until then
                    minVolumeGate: MinVolumeGate.Value,
                    crawlStepBps: CrawlStepBps.Value,
                    stickyDownTriggerDays: StickyDownDays.Value,
                    slumpThresholdPct: SlumpThresholdPct.Value,
                    floorMarginHundredths: FloorMarginHundredths.Value,
                    roundToWholeLims: RoundToWholeLims.Value);
                if (sug != null) priceSuggestions.Add(sug);
            }

            _rotation.Add(new Rotation(key, venue.Name, suggestion, priceSuggestions));

            decimal? prior = Data.Has($"profit.{key}") ? Data.Get<decimal?>($"profit.{key}", null) : null;
            if (prior is > 0 && suggestion.EstProfitPerDay > prior * (1 + (decimal)NotifyGain.Value / 100m))
                Ui.Notify("Menu Planner",
                    $"{venue.Name}: today's plan is worth ~{suggestion.EstProfitPerDay:0.00} lims/day, {(suggestion.EstProfitPerDay / prior - 1) * 100:0}% better than the stored plan.");

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
        IReadOnlyList<PriceSuggestion> Suggestions);
}