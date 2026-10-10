using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NivalisMenuPlanner.Core;

var cat = DishCatalog.LoadCsv(Path.Combine(AppContext.BaseDirectory, "Data", "dishdata.csv"));
Console.WriteLine($"Catalog loaded: {cat.Count} dishes\n");

var rules = new MenuRules(
    MaxSlots: 10,
    PreferBeverage: true, PreferEntree: true, PreferDessert: true,
    EntreeTypes: new[] { DishType.Meat, DishType.Fish, DishType.Seafood, DishType.Sandwich, DishType.Pizza, DishType.Soup },
    MaxPerType: 3, ChurnPenalty: 0.15f, StockDiscount: 0.2f,
    RoleBonus: 0.10f);

int failed = 0;
void Check(string label, bool ok, string evidence)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label} ({evidence})");
    if (!ok) failed++;
}

static int MarketPrice(string item)
{
    int sum = 0; foreach (var c in item) sum += c;
    return 50 + sum % 450;
}

VenueState Venue(string name, int lep, int grade, IEnumerable<string> knownIds)
{
    var ids = knownIds.ToArray();
    var prices = new Dictionary<string, int>();
    foreach (var dish in ids.Select(cat.ById).Where(d => d != null).Select(d => d!))
        foreach (var input in dish.Inputs)
            prices[input.Item] = MarketPrice(input.Item);
    return new VenueState(name, name, "Test District", 10, lep, grade,
        new Dictionary<string, int>(), prices,
        new[] { ("Workers", 0.5f), ("Hipsters", 0.3f), ("Elite", 0.2f) }, ids);
}

decimal AvgCost(MenuSuggestion s, VenueState v) =>
    s.Dishes.Count == 0 ? 0m : s.Dishes.Average(d => Planner.RawCostLims(d.Dish, v));

// T1 — profit-based fill: caps derived from cost keep every dish profitable,
//      so the menu equals the full profitable set (veto coverage lives in T8)
var v1 = Venue("T1-Diner", 2, 0,
    new[] { "Tonkotsu Ramen", "Beef Ramen", "Borsch", "Goulash", "Okroshka",
            "Fennel Soup with Bacon", "Sailor Soup", "Onion Rings", "Accordion Potatoes", "Beer" });
var s1 = Planner.Plan(cat, v1, rules, new MenuState(Array.Empty<string>(), 8f));
bool hasDrink = s1.Dishes.Any(d => d.Dish.Type == DishType.Drink);
bool hasEntree = s1.Dishes.Any(d => rules.EntreeTypes.Contains(d.Dish.Type));
Check("T1 beverage present", hasDrink, hasDrink ? "beverage present" : "none found");
Check("T1 entree present", hasEntree, $"{s1.Dishes.Count} dishes: " + string.Join(", ", s1.Dishes.Select(d => d.Dish.Name)));
var t1Expected = v1.KnownDishIds
    .Where(id => { var d = cat.ById(id)!; return d.PriceCapHundredths > Planner.RawCostLims(d, v1) * 100m; })
    .ToHashSet();
var t1Planned = s1.Dishes.Select(d => d.Dish.Id).ToHashSet();
Check("T1 fills all slots, set-equal to profitable set",
    t1Planned.SetEquals(t1Expected) && t1Planned.Count == 10,
    $"{t1Planned.Count}/10 planned");

// T2 — low Local Exclusivity Preference shifts toward cheap-input menus
var vLow = Venue("T2-LEP1", 1, 0, cat.Ids);
var vHigh = Venue("T2-LEP5", 5, 0, cat.Ids);
var sLow = Planner.Plan(cat, vLow, rules, new MenuState(Array.Empty<string>(), 8f));
var sHigh = Planner.Plan(cat, vHigh, rules, new MenuState(Array.Empty<string>(), 8f));
Check("T2 exclusivity shifts cost", AvgCost(sLow, vLow) < AvgCost(sHigh, vHigh),
    $"LEP1 avg input {AvgCost(sLow, vLow):0.00} vs LEP5 {AvgCost(sHigh, vHigh):0.00} lims");

// T3 — manager grade re-pricing: PriceMult math, per-dish cost effect, selection effect
Check("T3 PriceMult curve",
    Planner.PriceMult(0) == 1.00m && Planner.PriceMult(1) == 1.10m && Planner.PriceMult(10) == 0.80m
    && Planner.PriceMult(8) < Planner.PriceMult(1),
    "player=100%, g1=110%, g10=80%, monotonic down");

var vG0 = Venue("T3-G0", 5, 0, cat.Ids);
var vG1 = Venue("T3-G1", 5, 1, cat.Ids);
var vG8 = Venue("T3-G8", 5, 8, cat.Ids);
var sG1 = Planner.Plan(cat, vG1, rules, new MenuState(Array.Empty<string>(), 8f));
var sG8 = Planner.Plan(cat, vG8, rules, new MenuState(Array.Empty<string>(), 8f));

var beer = cat.ById("Beer")!;
Check("T3 grade re-prices a dish", Planner.RawCostLims(beer, vG8) < Planner.RawCostLims(beer, vG1),
    $"Beer: grade-8 pays {Planner.RawCostLims(beer, vG8):0.00} vs grade-1 {Planner.RawCostLims(beer, vG1):0.00} lims");

decimal AvgShelf(MenuSuggestion s) => s.Dishes.Count == 0 ? 0m
    : s.Dishes.Average(d => Planner.RawCostLims(d.Dish, vG0));
Check("T3 grade skews selection", AvgShelf(sG8) >= AvgShelf(sG1),
    $"shelf cost of picked menus: grade-8 {AvgShelf(sG8):0.00} vs grade-1 {AvgShelf(sG1):0.00} lims");

// T4 — payroll escrow math, exact
var staff = new[] { new StaffMember("A", 12m, 120), new StaffMember("B", 8m, 60), new StaffMember("C", 9m, 90) };
var ob = Payroll.Obligation(staff);
Check("T4 obligation math", ob == 45.5m, $"expected 45.50, got {ob}");
var sp = Payroll.Spendable(1000m, staff, 0.10m);
Check("T4 escrow math", sp == 949.95m, $"expected 949.95, got {sp}");

// T5 — rotation stability: yesterday's menu kept (churn penalty honored)
var vR = Venue("T5-Rot", 3, 4, cat.Ids);
var d1 = Planner.Plan(cat, vR, rules, new MenuState(Array.Empty<string>(), 8f));
var day1 = d1.Dishes.Select(d => d.Dish.Id).ToArray();
var d2 = Planner.Plan(cat, vR, rules, new MenuState(day1, 8f));
int kept = d2.Dishes.Count(d => day1.Contains(d.Dish.Id));
Check("T5 rotation stability", kept >= (int)(day1.Length * 0.7), $"{kept}/{day1.Length} dishes retained");

// T6 — pantry-aware planning: stock is seen, preferred, and nets out of the shopping list
//      (keys are ASSET names, matching the CSV and the game: Potatoes, Onions, Beef, Wheat)
var t6Known = new[] { "Tonkotsu Ramen", "Borsch", "Goulash", "Okroshka", "Fennel Soup with Bacon",
                      "Sailor Soup", "Onion Rings", "Accordion Potatoes", "Beer", "Kvass" };
var t6Prices = new Dictionary<string, int>();
foreach (var dish in t6Known.Select(cat.ById).Where(d => d != null).Select(d => d!))
    foreach (var input in dish.Inputs)
        t6Prices[input.Item] = MarketPrice(input.Item);
var t6Mix = new[] { ("Workers", 0.5f), ("Hipsters", 0.3f), ("Elite", 0.2f) };
var t6Stock = new Dictionary<string, int> { ["Potatoes"] = 50, ["Onions"] = 50, ["Beef"] = 30, ["Wheat"] = 30 };
var t6State = new MenuState(Array.Empty<string>(), 8f);

var v6Empty = new VenueState("T6-Empty", "T6-Empty", "Test District", 10, 2, 0,
    new Dictionary<string, int>(), t6Prices, t6Mix, t6Known);
var v6Stocked = new VenueState("T6-Stocked", "T6-Stocked", "Test District", 10, 2, 0,
    t6Stock, t6Prices, t6Mix, t6Known);

var s6Empty = Planner.Plan(cat, v6Empty, rules, t6State);
var s6Stocked = Planner.Plan(cat, v6Stocked, rules, t6State);

Check("T6 stock boosts profit", s6Stocked.EstProfitPerDay > s6Empty.EstProfitPerDay,
    $"stocked {s6Stocked.EstProfitPerDay:0.00} vs empty {s6Empty.EstProfitPerDay:0.00} lims/day");

int stockDishes = s6Stocked.Dishes.Count(d => d.Dish.Inputs.Any(i => t6Stock.ContainsKey(i.Item)));
Check("T6 planner spends pantry", stockDishes > 0, $"{stockDishes} menu dishes consume pantry stock");

bool potatoBought = s6Stocked.Shopping.Any(s => s.Item == "Potatoes");
Check("T6 shopping nets out pantry", !potatoBought,
    potatoBought ? "Potatoes still on shopping list" : "fully-stocked Potatoes not on shopping list");

// T7 — early-game tolerance: 4 unlocked recipes → 4-dish menu, roles arrive naturally
var s7 = Planner.Plan(cat, Venue("T7-Early", 2, 0,
    new[] { "Borsch", "Goulash", "Onion Rings", "Beer" }),
    rules, new MenuState(Array.Empty<string>(), 8f));
Check("T7 plans with what exists", s7.Dishes.Count == 4,
    $"{s7.Dishes.Count} dishes from 4 unlocked; warnings: {string.Join(" | ", s7.Warnings)}");
bool t7Roles = s7.Dishes.Any(d => d.Dish.Type == DishType.Drink)
             && s7.Dishes.Any(d => rules.EntreeTypes.Contains(d.Dish.Type));
Check("T7 roles hold in small menus", t7Roles, "beverage + entrée present");

// T8 — price point outranks everything: losing dishes stay off even with empty slots
var t8Prices = new Dictionary<string, int>
{ ["Tomatoes"] = 8000, ["Potatoes"] = 8000, ["Beef"] = 8000, ["Onions"] = 8000, ["Flour"] = 8000, ["Wheat"] = 8000 };
var v8 = new VenueState("T8-Ruin", "T8-Ruin", "Test District", 10, 2, 0,
    new Dictionary<string, int>(), t8Prices,
    new[] { ("Workers", 0.5f), ("Hipsters", 0.3f), ("Elite", 0.2f) },
    new[] { "Borsch", "Onion Rings", "Beer" });
var s8 = Planner.Plan(cat, v8, rules, new MenuState(Array.Empty<string>(), 8f));
Check("T8 profit bar excludes losers (and preferences)", s8.Dishes.Count == 0,
    $"{s8.Dishes.Count} dishes planned; warnings: {string.Join(" | ", s8.Warnings)}");

// T9 — role bonus tips close calls: Borsch takes slot 1; Beer's base score sits just under
//      Onion Rings', so the 10% beverage lift flips slot 2 (numbers in evidence when run)
var t9Prices = new Dictionary<string, int>
{ ["Tomatoes"] = 100, ["Potatoes"] = 100, ["Beef"] = 100, ["Onions"] = 195, ["Flour"] = 195, ["Wheat"] = 100 };
var v9 = new VenueState("T9-Tie", "T9-Tie", "Test District", 2, 3, 0,
    new Dictionary<string, int>(), t9Prices,
    new[] { ("Workers", 0.5f), ("Hipsters", 0.3f), ("Elite", 0.2f) },
    new[] { "Borsch", "Onion Rings", "Beer" });
var s9 = Planner.Plan(cat, v9, rules, new MenuState(Array.Empty<string>(), 8f));
Check("T9 role bonus tips close calls",
    s9.Dishes.Count == 2 && s9.Dishes.Any(d => d.Dish.Type == DishType.Drink),
    $"{string.Join(", ", s9.Dishes.Select(d => d.Dish.Name))}");
Check("T9 close loser yields",
    !s9.Dishes.Any(d => d.Dish.Id == "Onion Rings"),
    "Onion Rings displaced by bonus-lifted Beer");

// T10 — preference yields to a clearly better dish: wheat at 500 pushes Beer under the profit bar
var t10Prices = new Dictionary<string, int>(t9Prices) { ["Wheat"] = 500 };
var v10 = new VenueState("T10-NoTie", "T10-NoTie", "Test District", 2, 3, 0,
    new Dictionary<string, int>(), t10Prices,
    new[] { ("Workers", 0.5f), ("Hipsters", 0.3f), ("Elite", 0.2f) },
    new[] { "Borsch", "Onion Rings", "Beer" });
var s10 = Planner.Plan(cat, v10, rules, new MenuState(Array.Empty<string>(), 8f));
Check("T10 preference yields to better dish",
    s10.Dishes.Count == 2 && !s10.Dishes.Any(d => d.Dish.Type == DishType.Drink),
    $"{string.Join(", ", s10.Dishes.Select(d => d.Dish.Name))}");
Check("T10 notes the missing beverage",
    s10.Warnings.Any(w => w.Contains("beverage")),
    string.Join(" | ", s10.Warnings));

// T11 — craft-chain inputs: Beer Soup needs Beer, which no vendor sells. It must stay
// schedulable by brewing Beer from Wheat, costed at Wheat's price, and the shopping list
// must name the base ingredient (Wheat) — never the unbuyable crafted one (Beer).
var t11Prices = new Dictionary<string, int>
{ // 'Beer' deliberately absent from the market
    ["Wheat"] = 100,
    ["Blue Cheese"] = 200,
    ["Bacon"] = 200,
    ["Onions"] = 100,
    ["Wheat Bread"] = 150,
    ["Tomatoes"] = 150,
    ["Potatoes"] = 100,
    ["Beef"] = 400
};
var v11 = new VenueState("T11-Craft", "T11-Craft", "Test District", 10, 3, 0,
    new Dictionary<string, int>(), t11Prices, t6Mix,
    new[] { "Beer Soup", "Beer", "Borsch" });
var s11 = Planner.Plan(cat, v11, rules, new MenuState(Array.Empty<string>(), 8f));
Check("T11 craft inputs keep dish schedulable",
    s11.Dishes.Any(d => d.Dish.Id == "Beer Soup"),
    $"menu: {string.Join(", ", s11.Dishes.Select(d => d.Dish.Name))}");
var beerSoup = s11.Dishes.FirstOrDefault(d => d.Dish.Id == "Beer Soup");
Check("T11 crafted input priced via its recipe",
    beerSoup?.CostHundredths == 750,
    $"Beer Soup cost {beerSoup?.CostHundredths.ToString() ?? "n/a"} (want 750: Beer-as-Wheat 100 + Blue Cheese 200 + Bacon 200 + Onions 100 + Wheat Bread 150)");
Check("T11 shopping expands crafted inputs",
    !s11.Shopping.Any(s => s.Item == "Beer") && s11.Shopping.Any(s => s.Item == "Wheat" && s.Needed >= 10),
    "shopping: " + string.Join(", ", s11.Shopping.Select(s => $"{s.Item} x{s.Needed}")));
var v11b = new VenueState("T11-NoRecipe", "T11-NoRecipe", "Test District", 10, 3, 0,
    new Dictionary<string, int>(), t11Prices, t6Mix,
    new[] { "Beer Soup", "Borsch" });
var s11b = Planner.Plan(cat, v11b, rules, new MenuState(Array.Empty<string>(), 8f));
Check("T11 unknown craft recipe blocks the dish",
    !s11b.Dishes.Any(d => d.Dish.Id == "Beer Soup") && s11b.Warnings.Any(w => w.Contains("skipped")),
    $"menu: {string.Join(", ", s11b.Dishes.Select(d => d.Dish.Name))}; warnings: {string.Join(" | ", s11b.Warnings)}");

// T12 — district canon: keys match the vendors dump ('Spire', not 'The Spire'); mixes skew
// the way the districts read (Spire elite-heavy, Sewers worker-heavy; unknown -> even split)
var spireMix = cat.MixFor("Spire");
var sewersMix = cat.MixFor("Sewers");
bool spireEliteHeavy = spireMix.First(t => t.Group == "Elite").Weight > 0.5f;
bool sewersWorkerHeavy = sewersMix.First(t => t.Group == "Workers").Weight > 0.6f;
bool oldKeyIsDefault = cat.MixFor("The Spire").All(t => Math.Abs(t.Weight - 1f / 3f) < 0.01f);
Check("T12 district canon and mix skew",
    spireEliteHeavy && sewersWorkerHeavy && oldKeyIsDefault,
    $"Spire elite {spireMix.First(t => t.Group == "Elite").Weight:0.00}, Sewers workers {sewersMix.First(t => t.Group == "Workers").Weight:0.00}, 'The Spire' falls back to even mix");

// T15 — capacity guard: the binding input caps servings. Stock for 3 Borsch
//      (Tomatoes 3 / Potatoes plenty / Beef plenty) -> ServingsPossible == 3.
//      The overbooking scenario you watched in-game, as a permanent regression test.
var v15 = new VenueState("T15-Cap", "T15-Cap", "Test District", 10, 3, 0,
    new Dictionary<string, int> { ["Tomatoes"] = 3, ["Potatoes"] = 50, ["Beef"] = 50 },
    new Dictionary<string, int> { ["Tomatoes"] = 150, ["Potatoes"] = 100, ["Beef"] = 400 },
    t6Mix,
    new[] { "Borsch", "Beer", "Onion Rings" });
var borsch = cat.ById("Borsch")!;
int borschServings = Planner.ServingsPossible(borsch, v15, cat);
Check("T15 capacity guard: binding input caps servings",
    borschServings == 3,
    $"Borsch servings possible: {borschServings} (Tomatoes x3 is the binding input; Potatoes/Beef plentiful)");

// T15b — guard returns 0 when a binding input is empty (stock says no Borsch today)
var v15b = new VenueState("T15b-Zero", "T15b-Zero", "Test District", 10, 3, 0,
    new Dictionary<string, int> { ["Potatoes"] = 50, ["Beef"] = 50 },
    new Dictionary<string, int> { ["Tomatoes"] = 150, ["Potatoes"] = 100, ["Beef"] = 400 },
    t6Mix,
    new[] { "Borsch", "Beer" });
int borschZero = Planner.ServingsPossible(borsch, v15b, cat);
Check("T15b capacity guard: zero stock means zero servings",
    borschZero == 0,
    $"Borsch servings with no Tomatoes: {borschZero}");

// T15c — escrow view: committed amounts subtract from stock; uncommitted stock passes through
var escrowStock = new Dictionary<string, int> { ["Tomatoes"] = 10, ["Potatoes"] = 50, ["Beef"] = 50 };
var v15c = new VenueState("T15c-Escrow", "T15c-Escrow", "Test District", 10, 3, 0,
    escrowStock,
    new Dictionary<string, int> { ["Tomatoes"] = 150, ["Potatoes"] = 100, ["Beef"] = 400 },
    t6Mix,
    new[] { "Borsch", "Beer" });
int freeTomatoes = Planner.AvailableStock(v15c, "Tomatoes", new Dictionary<string, int> { ["Tomatoes"] = 4 });
int allTomatoes = Planner.AvailableStock(v15c, "Tomatoes", null);
Check("T15c escrow view: committed stock is subtracted",
    freeTomatoes == 6 && allTomatoes == 10,
    $"Tomatoes free after committing 4: {freeTomatoes}; with no holds: {allTomatoes}");

// T16 — craft-chain capacity: Beer Soup needs Beer (no stock), Beer needs Wheat (stock 5)
//      -> Beer Soup servable from Wheat is 5 / 1 per soup (1 Beer per soup, 1 Wheat per Beer)
var v16 = new VenueState("T16-Chain", "T16-Chain", "Test District", 10, 3, 0,
    new Dictionary<string, int>
    {
        ["Wheat"] = 5,
        ["Blue Cheese"] = 50,
        ["Bacon"] = 50,
        ["Onions"] = 50,
        ["Wheat Bread"] = 50
    },
    new Dictionary<string, int>
    {
        ["Wheat"] = 100,
        ["Blue Cheese"] = 200,
        ["Bacon"] = 200,
        ["Onions"] = 100,
        ["Wheat Bread"] = 150
    },
    t6Mix,
    new[] { "Beer Soup", "Beer", "Borsch" });
int beerSoupServings = Planner.ServingsPossible(cat.ById("Beer Soup")!, v16, cat);
Check("T16 capacity guard: craft-chain depth (Beer Soup via Wheat)",
    beerSoupServings == 5,
    $"Beer Soup servings from Wheat x5: {beerSoupServings} (chain: Beer Soup <- Beer <- Wheat)");

Console.WriteLine($"\n{(failed == 0 ? "ALL TESTS PASS" : failed + " TEST(S) FAILED — paste output to me")}");
return failed;