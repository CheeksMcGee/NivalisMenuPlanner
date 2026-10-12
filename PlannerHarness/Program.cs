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

// Plentiful pantry: every input of the known dishes, 40 each. 40 (not 20) because the
// escrow ledger commits sequentially - two dishes claiming one input at ~10 orders each
// need ~20 headroom; 40 keeps 'plentiful' true under honest competition (the Step 4a
// T1 lesson: at 20, the tenth dish's claim can exceed what nine left).
Dictionary<string, int> TestStock(IEnumerable<string> knownIds)
{
    var stock = new Dictionary<string, int>();
    foreach (var dish in knownIds.Select(cat.ById).Where(d => d != null).Select(d => d!))
        foreach (var input in dish.Inputs)
            stock[input.Item] = Math.Max(stock.GetValueOrDefault(input.Item), 40);
    return stock;
}

VenueState Venue(string name, int lep, int grade, IEnumerable<string> knownIds, bool stockIt = true)
{
    var ids = knownIds.ToArray();
    var prices = new Dictionary<string, int>();
    foreach (var dish in ids.Select(cat.ById).Where(d => d != null).Select(d => d!))
        foreach (var input in dish.Inputs)
            prices[input.Item] = MarketPrice(input.Item);
    return new VenueState(name, name, "Test District", 10, lep, grade,
        stockIt ? TestStock(ids) : new Dictionary<string, int>(), prices,
        new[] { ("Workers", 0.5f), ("Hipsters", 0.3f), ("Elite", 0.2f) }, ids);
}

decimal AvgCost(MenuSuggestion s, VenueState v) =>
    s.Dishes.Count == 0 ? 0m : s.Dishes.Average(d => Planner.RawCostLims(d.Dish, v));

// T1 — profit-based fill with a plentiful pantry (40/input: plentiful under the ledger)
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

// T3 — manager grade re-pricing
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

// T5 — rotation stability
var vR = Venue("T5-Rot", 3, 4, cat.Ids);
var d1 = Planner.Plan(cat, vR, rules, new MenuState(Array.Empty<string>(), 8f));
var day1 = d1.Dishes.Select(d => d.Dish.Id).ToArray();
var d2 = Planner.Plan(cat, vR, rules, new MenuState(day1, 8f));
int kept = d2.Dishes.Count(d => day1.Contains(d.Dish.Id));
Check("T5 rotation stability", kept >= (int)(day1.Length * 0.7), $"{kept}/{day1.Length} dishes retained");

// T6 — pantry-aware planning
var t6Known = new[] { "Tonkotsu Ramen", "Borsch", "Goulash", "Okroshka", "Fennel Soup with Bacon",
                      "Sailor Soup", "Onion Rings", "Accordion Potatoes", "Beer", "Kvass" };
var t6Prices = new Dictionary<string, int>();
foreach (var dish in t6Known.Select(cat.ById).Where(d => d != null).Select(d => d!))
    foreach (var input in dish.Inputs)
        t6Prices[input.Item] = MarketPrice(input.Item);
var t6Mix = new[] { ("Workers", 0.5f), ("Hipsters", 0.3f), ("Elite", 0.2f) };
var t6State = new MenuState(Array.Empty<string>(), 8f);

var v6Empty = new VenueState("T6-Empty", "T6-Empty", "Test District", 10, 2, 0,
    new Dictionary<string, int>(), t6Prices, t6Mix, t6Known);
var t6StockedStock = new Dictionary<string, int> { ["Potatoes"] = 50, ["Onions"] = 50, ["Beef"] = 30, ["Wheat"] = 30 };
foreach (var kv in TestStock(t6Known))
    t6StockedStock[kv.Key] = Math.Max(t6StockedStock.GetValueOrDefault(kv.Key), kv.Value);
var v6Stocked = new VenueState("T6-Stocked", "T6-Stocked", "Test District", 10, 2, 0,
    t6StockedStock, t6Prices, t6Mix, t6Known);

var s6Empty = Planner.Plan(cat, v6Empty, rules, t6State);
var s6Stocked = Planner.Plan(cat, v6Stocked, rules, t6State);

Check("T6 stock boosts profit", s6Stocked.EstProfitPerDay > s6Empty.EstProfitPerDay,
    $"stocked {s6Stocked.EstProfitPerDay:0.00} vs empty {s6Empty.EstProfitPerDay:0.00} lims/day");

int stockDishes = s6Stocked.Dishes.Count(d => d.Dish.Inputs.Any(i => t6StockedStock.ContainsKey(i.Item)));
Check("T6 planner spends pantry", stockDishes > 0, $"{stockDishes} menu dishes consume pantry stock");

bool potatoBought = s6Stocked.Shopping.Any(s => s.Item == "Potatoes");
Check("T6 shopping nets out pantry", !potatoBought,
    potatoBought ? "Potatoes still on shopping list" : "fully-stocked Potatoes not on shopping list");

// T7 — early-game tolerance
var s7 = Planner.Plan(cat, Venue("T7-Early", 2, 0,
    new[] { "Borsch", "Goulash", "Onion Rings", "Beer" }),
    rules, new MenuState(Array.Empty<string>(), 8f));
Check("T7 plans with what exists", s7.Dishes.Count == 4,
    $"{s7.Dishes.Count} dishes from 4 unlocked; warnings: {string.Join(" | ", s7.Warnings)}");
bool t7Roles = s7.Dishes.Any(d => d.Dish.Type == DishType.Drink)
             && s7.Dishes.Any(d => rules.EntreeTypes.Contains(d.Dish.Type));
Check("T7 roles hold in small menus", t7Roles, "beverage + entrée present");

// T8 — ruin prices + empty pantry
var v8 = Venue("T8-Ruin", 2, 0, new[] { "Borsch", "Onion Rings", "Beer" }, stockIt: false);
var s8 = Planner.Plan(cat, v8, rules, new MenuState(Array.Empty<string>(), 8f));
Check("T8 profit bar excludes losers (and preferences)", s8.Dishes.Count == 0,
    $"{s8.Dishes.Count} dishes planned; warnings: {string.Join(" | ", s8.Warnings)}");

// T9 — role bonus tips close calls (discounted-margin tuning, LEP 5)
var t9Known = new[] { "Borsch", "Onion Rings", "Beer" };
var t9Prices = new Dictionary<string, int>
{
    ["Tomatoes"] = 100,
    ["Potatoes"] = 100,
    ["Beef"] = 100,
    ["Onions"] = 750,
    ["Flour"] = 750,
    ["Wheat"] = 100
};
var v9 = new VenueState("T9-Tie", "T9-Tie", "Test District", 2, 5, 0,
    TestStock(t9Known), t9Prices,
    new[] { ("Workers", 0.5f), ("Hipsters", 0.3f), ("Elite", 0.2f) }, t9Known);
var s9 = Planner.Plan(cat, v9, rules, new MenuState(Array.Empty<string>(), 8f));
Check("T9 role bonus tips close calls",
    s9.Dishes.Count == 2 && s9.Dishes.Any(d => d.Dish.Type == DishType.Drink),
    $"{string.Join(", ", s9.Dishes.Select(d => d.Dish.Name))}");
Check("T9 close loser yields",
    !s9.Dishes.Any(d => d.Dish.Id == "Onion Rings"),
    "Onion Rings displaced by bonus-lifted Beer");

// T10 — preference yields to a clearly better dish
var t10Prices = new Dictionary<string, int>(t9Prices) { ["Wheat"] = 500 };
var v10 = new VenueState("T10-NoTie", "T10-NoTie", "Test District", 2, 5, 0,
    TestStock(t9Known), t10Prices,
    new[] { ("Workers", 0.5f), ("Hipsters", 0.3f), ("Elite", 0.2f) }, t9Known);
var s10 = Planner.Plan(cat, v10, rules, new MenuState(Array.Empty<string>(), 8f));
Check("T10 preference yields to better dish",
    s10.Dishes.Count == 2 && !s10.Dishes.Any(d => d.Dish.Type == DishType.Drink),
    $"{string.Join(", ", s10.Dishes.Select(d => d.Dish.Name))}");
Check("T10 notes the missing beverage",
    s10.Warnings.Any(w => w.Contains("beverage")),
    string.Join(" | ", s10.Warnings));

// T11 — craft-chain inputs: Beer Soup needs Beer, which no vendor sells and which is
//        deliberately NOT in the pantry. The escrow ledger commits Beer Soup's Wheat
//        claim through the chain (and Beer claims Wheat too); Beer Soup's rescue entry
//        computed against post-commitment Wheat finally exceeds what remains -> Wheat
//        appears on the shopping list. This is the Step 3 structural-emptiness discovery
//        resolving exactly as predicted.
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
var t11Known = new[] { "Beer Soup", "Beer", "Borsch" };
var t11Stock = new Dictionary<string, int>
{
    ["Wheat"] = 10,
    ["Blue Cheese"] = 20,
    ["Bacon"] = 20,
    ["Onions"] = 20,
    ["Wheat Bread"] = 20
};   // NO Beer: the crafted item stays unstocked
var v11 = new VenueState("T11-Craft", "T11-Craft", "Test District", 10, 3, 0,
    t11Stock, t11Prices, t6Mix, t11Known);
var s11 = Planner.Plan(cat, v11, rules, new MenuState(Array.Empty<string>(), 8f));
Check("T11 craft inputs keep dish schedulable",
    s11.Dishes.Any(d => d.Dish.Id == "Beer Soup"),
    $"menu: {string.Join(", ", s11.Dishes.Select(d => d.Dish.Name))}");
var beerSoup = s11.Dishes.FirstOrDefault(d => d.Dish.Id == "Beer Soup");
Check("T11 crafted input priced via its recipe",
    beerSoup?.CostHundredths == 230,
    $"Beer Soup cost {beerSoup?.CostHundredths.ToString() ?? "n/a"} (want 230: Beer-as-Wheat 100 at full price + discounted Blue Cheese 40 + Bacon 40 + Onions 20 + Wheat Bread 30)");
Check("T11 escrow surfaces the Wheat gap (the Step 3 prediction, landed)",
    s11.Shopping.Any(s => s.Item == "Wheat" && s.Needed >= 1) && !s11.Shopping.Any(s => s.Item == "Beer"),
    "shopping: " + string.Join(", ", s11.Shopping.Select(s => $"{s.Item} x{s.Needed}")));
// T11b — recipe-block premise: bare pantry, no Beer recipe
var v11b = new VenueState("T11-NoRecipe", "T11-NoRecipe", "Test District", 10, 3, 0,
    new Dictionary<string, int>(), t11Prices, t6Mix, new[] { "Beer Soup", "Borsch" });
var s11b = Planner.Plan(cat, v11b, rules, new MenuState(Array.Empty<string>(), 8f));
Check("T11 unknown craft recipe blocks the dish",
    !s11b.Dishes.Any(d => d.Dish.Id == "Beer Soup") && s11b.Warnings.Any(w => w.Contains("skipped")),
    $"menu: {string.Join(", ", s11b.Dishes.Select(d => d.Dish.Name))}; warnings: {string.Join(" | ", s11b.Warnings)}");

// T12 — district canon
var spireMix = cat.MixFor("Spire");
var sewersMix = cat.MixFor("Sewers");
bool spireEliteHeavy = spireMix.First(t => t.Group == "Elite").Weight > 0.5f;
bool sewersWorkerHeavy = sewersMix.First(t => t.Group == "Workers").Weight > 0.6f;
bool oldKeyIsDefault = cat.MixFor("The Spire").All(t => Math.Abs(t.Weight - 1f / 3f) < 0.01f);
Check("T12 district canon and mix skew",
    spireEliteHeavy && sewersWorkerHeavy && oldKeyIsDefault,
    $"Spire elite {spireMix.First(t => t.Group == "Elite").Weight:0.00}, Sewers workers {sewersMix.First(t => t.Group == "Workers").Weight:0.00}, 'The Spire' falls back to even mix");

// T15 — capacity guard: binding input caps servings
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

// T15b — zero stock means zero servings
var v15b = new VenueState("T15b-Zero", "T15b-Zero", "Test District", 10, 3, 0,
    new Dictionary<string, int> { ["Potatoes"] = 50, ["Beef"] = 50 },
    new Dictionary<string, int> { ["Tomatoes"] = 150, ["Potatoes"] = 100, ["Beef"] = 400 },
    t6Mix,
    new[] { "Borsch", "Beer" });
int borschZero = Planner.ServingsPossible(borsch, v15b, cat);
Check("T15b capacity guard: zero stock means zero servings",
    borschZero == 0,
    $"Borsch servings with no Tomatoes: {borschZero}");

// T15c — escrow view: committed stock subtracts
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

// T16 — craft-chain capacity
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

// T17a — ESCROW COMPETITION: two dishes share Tomatoes; stock 15 serves both demands
//        (Borsch ~6, Summer Salad ~5). The ledger's claims are sequential and honest;
//        both place. The pre-ledger planner scored each in isolation - both "fine" -
//        the ledger makes the shared claim arithmetic visible.
var t17aKnown = new[] { "Borsch", "Summer Salad" };
var t17aStock = new Dictionary<string, int>
{
    ["Tomatoes"] = 15,
    ["Potatoes"] = 40,
    ["Beef"] = 40,
    ["Cucumbers"] = 40,
    ["Chicken"] = 40,
    ["Cheese"] = 40,
    ["Onions"] = 40
};
var t17aPrices = new Dictionary<string, int>
{
    ["Tomatoes"] = 150,
    ["Potatoes"] = 100,
    ["Beef"] = 400,
    ["Cucumbers"] = 100,
    ["Chicken"] = 300,
    ["Cheese"] = 150,
    ["Onions"] = 100
};
var v17a = new VenueState("T17a-Compete", "T17a-Compete", "Test District", 10, 3, 0,
    t17aStock, t17aPrices, t6Mix, t17aKnown);
var s17a = Planner.Plan(cat, v17a, rules, new MenuState(Array.Empty<string>(), 8f));
bool bothPlaced17a = s17a.Dishes.Count(d => d.Dish.Id is "Borsch" or "Summer Salad") == 2;
Check("T17a escrow: both shared-input dishes place (stock serves both)",
    bothPlaced17a,
    $"menu: {string.Join(", ", s17a.Dishes.Select(d => d.Dish.Name))}");

// T17d — ACTUAL-ORDERS COMMITMENT: tighten stock so competition binds. Tomatoes 8:
//        Borsch (~6 raw) places first, commits 6, leaving 2. Summer Salad sees 2
//        tomato-capable servings: its orders cap at 2 and the ledger's arithmetic is
//        observable. The anti-overcommit invariant must hold regardless of which
//        dishes place: planned tomato orders across the menu never exceed stock.
var t17dStock = new Dictionary<string, int>(t17aStock) { ["Tomatoes"] = 8 };
var v17d = new VenueState("T17d-Bind", "T17d-Bind", "Test District", 10, 3, 0,
    t17dStock, t17aPrices, t6Mix, t17aKnown);
var s17d = Planner.Plan(cat, v17d, rules, new MenuState(Array.Empty<string>(), 8f));
var tomatoLines = s17d.Dishes.Where(d => d.Dish.Inputs.Any(i => i.Item == "Tomatoes")).ToList();
int plannedTomatoOrders = tomatoLines.Sum(d => (int)Math.Ceiling(d.ExpectedOrders));
bool noOvercommit = plannedTomatoOrders <= 8;
Check("T17d ledger never over-commits (planned tomato orders <= stock)",
    noOvercommit,
    $"planned tomato orders {plannedTomatoOrders} vs stock 8; menu: {string.Join(", ", s17d.Dishes.Select(d => d.Dish.Name))}; " +
    $"orders: {string.Join(", ", s17d.Dishes.Select(d => $"{d.Dish.Name} {d.ExpectedOrders:0.0}"))}");

// T17b — PER-LIM URGENCY (ruling 1): scarce Tomatoes (4) vs ~10 raw demand -> a rescue
//         entry carrying protected profit. Borsch's contribution dominates the protected
//         amount (higher margin), and the entry survives the floor. Check: the tomato
//         rescue exists, carries protection >= the floor, and the list orders by
//         protected-per-lim (single-entry here; ordering vs penny-restocks is exercised
//         by the floor removing the pennies in T17c).
var t17bStock = new Dictionary<string, int>(t17aStock) { ["Tomatoes"] = 4 };
var v17b = new VenueState("T17b-Urgency", "T17b-Urgency", "Test District", 10, 3, 0,
    t17bStock, t17aPrices, t6Mix, t17aKnown);
var s17bR = Planner.Plan(cat, v17b, rules, new MenuState(Array.Empty<string>(), 8f));
var t17bList = s17bR.Shopping.Where(s => s.Item == "Tomatoes").ToList();
bool t17bOk = t17bList.Count == 1
           && t17bList[0].Needed >= 1
           && t17bList[0].ProtectedHundredths >= rules.MinErrandValueHundredths;
Check("T17b urgency: rescue entries carry protected profit",
    t17bOk,
    $"shopping: {string.Join(", ", s17bR.Shopping.Select(s => $"{s.Item} x{s.Needed} (protects {s.ProtectedHundredths / 100m:0.00} lims)"))}");

// T17c — ERRAND FLOOR (ruling: 5 lims = 500 hundredths): a 1-2 unit gap protecting only
//         a couple lims drops off. Summer Salad alone: raw demand ~5 (worker-mix), stock
//         6 tomatoes -> salad fully served, list empty; then a tight venue: stock 4 ->
//         gap ~1 unit protecting ~salad margin on 1 order (~200 hundredths) -> dropped.
var t17cKnown = new[] { "Summer Salad" };
var t17cStock = new Dictionary<string, int>
{
    ["Tomatoes"] = 4,
    ["Chicken"] = 40,
    ["Cucumbers"] = 40,
    ["Cheese"] = 40
};
var v17c = new VenueState("T17c-Floor", "T17c-Floor", "Test District", 10, 3, 0,
    t17cStock, t17aPrices, t6Mix, t17cKnown);
var s17c = Planner.Plan(cat, v17c, rules, new MenuState(Array.Empty<string>(), 8f));
bool noPennyErrands = s17c.Shopping.All(s => s.ProtectedHundredths >= rules.MinErrandValueHundredths);
Check("T17c errand floor: entries protecting < 5 lims drop off the list",
    noPennyErrands,
    $"shopping: {string.Join(", ", s17c.Shopping.Select(s => $"{s.Item} x{s.Needed} (protects {s.ProtectedHundredths / 100m:0.00})"))}");
// T18 — DemandTuner: learns per-dish levels from demand-clean records (Q1/Q2).
// Fixture semantics: CLEAN = SoldOut false (sold < cooked: kitchen plated
// headroom, demand wasn't supply-capped). Sold == Cooked means SOLD OUT
// per SaleRecord.SoldOut — capacity was binding. The first draft of these
// fixtures marked sold==cooked as "clean" and was wrong about its own type.
var t18Records = new List<SaleRecord> {
    new(70, 2500, 9,  11, 0, null),    // clean: 9 of 11 plated sold
    new(71, 2500, 17, 19, 0, null),   // clean: 17 of 19
    new(72, 2500, 21, 23, 0, null),   // clean: 21 of 23
};
double t18Learned = DemandTuner.ExpectedOrders("Fish Salad", t18Records, fallbackScale: 8.0);
Check("T18a tuner averages clean records",
    Math.Abs(t18Learned - 15.667) < 0.01,                     // (9+17+21)/3
    $"learned {t18Learned:0.00} from 9/17/21");

// T18b — all sold-out: max observed as floor, not the censored mean.
var t18B = new List<SaleRecord> {
    new(70, 800, 30, 30, 0, null),
    new(71, 800, 33, 33, 0, null),     // SoldOut true
    new(72, 800, 31, 31, 0, null),    // SoldOut true
};
double t18Floor = DemandTuner.ExpectedOrders("Beer", t18B, fallbackScale: 8.0);
Check("T18b sold-out days return max as floor (capacity censoring)",
    Math.Abs(t18Floor - 33.0) < 0.01,
    $"all-sold-out window returns {t18Floor:0.0} (max observed), not the mean {(30.0 + 33.0 + 31.0) / 3:0.0}");

// T18c — mixed window: clean days average; sold-out day excluded.
var t18C = new List<SaleRecord> {
    new(70, 2600, 22, 24, 0, null),    // clean
    new(71, 2600, 29, 31, 0, null),    // clean
    new(72, 2600, 28, 28, 0, null),    // SoldOut true
};
double t18Mixed = DemandTuner.ExpectedOrders("Oyster Shooter", t18C, fallbackScale: 8.0);
Check("T18c mixed window averages clean days only",
    Math.Abs(t18Mixed - 25.5) < 0.01,                          // (22+29)/2
    $"mixed window returns {t18Mixed:0.00} ((22+29)/2; sold-out day excluded)");

// T18d — cold start and pruning
double t18Cold = DemandTuner.ExpectedOrders("Anything", new List<SaleRecord>(), fallbackScale: 8.0);
Check("T18d cold start falls back to baseline",
    Math.Abs(t18Cold - 8.0) < 0.01,
    $"empty history -> {t18Cold:0.0} (config DemandScale)");
bool t18OldKept = DemandTuner.ShouldRetain(70, 72);   // 2 days back -> keep
bool t18OldDropped = !DemandTuner.ShouldRetain(69, 72); // 3 days back -> prune
Check("T18d retention window prunes old records",
    t18OldKept && t18OldDropped,
    $"day 70 retained vs today 72: {t18OldKept}; day 69 pruned: {t18OldDropped}");
// T19 — PricingController: the doctrine's decision table.
// Fixture convention: days (d-2, d-1, d) so the 3-day window sees all three;
// "clean" means sold < cooked (not SoldOut), matching SaleRecord semantics.

// T19a — capacity-bound: no price move (row 1). Sold 30 vs platable 25 -> supply-bound.
var t19aRecords = new List<SaleRecord> {
    new(70, 800, 25, 27, 0, null),
    new(71, 800, 30, 31, 0, null),   // sold 30, cooked 31: clean (30 < 31, not SoldOut)
    new(72, 800, 30, 32, 0, null),   // clean
};
var t19aDish = cat.ById("Beer")!;    // cap 800 (city-measured in production; CSV estimate here is 350 - the catalog holds CSV caps in the harness)
// NOTE: the harness catalog carries CSV-estimated caps; the controller uses
// dish.PriceCapHundredths as the CEILING, which is what it is in production
// (measured caps override at GameReady). For T19a the ceiling is irrelevant
// (capacity-bound -> null before any price math).
var t19aSug = PricingController.Suggest(t19aDish, currentPrice: 800, records: t19aRecords,
    costHundredths: 250, platableToday: 25, districtMedian: 800, established: true);
Check("T19a capacity-bound: no price move",
    t19aSug == null,
    t19aSug == null ? "supply was binding (sold 30 vs platable 25) -> supply grows, price holds" : $"unexpected {t19aSug.Diagnosis}");

// T19b — throughput-bound: walk-outs present -> service domain, no price move (row 2).
var t19bRecords = new List<SaleRecord> {
    new(70, 2500, 20, 21, 1, null),   // walked 1
    new(71, 2500, 22, 25, 3, null),   // walked 3
    new(72, 2500, 21, 23, 2, null),   // walked 2
};
var t19bDish = cat.ById("Fish Salad")!;
var t19bSug = PricingController.Suggest(t19bDish, currentPrice: 2500, records: t19bRecords,
    costHundredths: 700, platableToday: 50, districtMedian: 3100, established: true);
Check("T19b throughput-bound (walk-outs): no price move",
    t19bSug == null,
    t19bSug == null ? "plates died at the dishwasher -> staffing domain, price holds" : $"unexpected {t19bSug.Diagnosis}");

// T19c — established catch-up: 2500 vs median 3100 -> suggest the anchor exactly.
// Arithmetic: 2500 < 3100; gap 600 = 19.4% of anchor -> NOT at market -> jump to 3100.
var t19cRecords = new List<SaleRecord> {
    new(70, 2500, 17, 19, 0, null),
    new(71, 2500, 21, 23, 0, null),
    new(72, 2500, 20, 22, 0, null),   // no slump: 20 >= 19*0.8
};
var t19cSug = PricingController.Suggest(t19bDish, currentPrice: 2500, records: t19cRecords,
    costHundredths: 700, platableToday: 50, districtMedian: 3100, established: true);
Check("T19c established: catch up to district median",
    t19cSug != null && t19cSug.SuggestedPriceHundredths == 3100 && t19cSug.Diagnosis.Contains("catch up"),
    t19cSug == null ? "no suggestion (unexpected)" : $"{t19cSug.Diagnosis}: {t19cSug.CurrentPriceHundredths} -> {t19cSug.SuggestedPriceHundredths}");

// T19d — entry venue: same data, but NOT established -> anchor at 95% of median (2945).
var t19dSug = PricingController.Suggest(t19bDish, currentPrice: 2500, records: t19cRecords,
    costHundredths: 700, platableToday: 50, districtMedian: 3100, established: false);
Check("T19d entry: open at district floor (95% of median)",
    t19dSug != null && t19dSug.SuggestedPriceHundredths == 2945 && t19dSug.Diagnosis.Contains("entry"),
    t19dSug == null ? "no suggestion (unexpected)" : $"{t19dSug.Diagnosis}: 2500 -> {t19dSug.SuggestedPriceHundredths}");

// T19e — thin district coverage (null median): crawl only. 2500 -> 2500+125 = 2625.
// Arithmetic: step = 2500 * 500 / 10000 = 125. Cap: Fish Salad's CSV cap 3705... wait,
// CSV estimate; the catalog has 3705? No - CSV cap for Fish Salad from the generator
// (Salad factor 1.9 x input cost ~3.3 lims) -> ~620. The harness catalog holds CSV
// caps, so 2625 may exceed the cap. Use a dish with a high cap: Beer at 350 also fails.
// RESOLUTION: the controller's ceiling is the catalog cap; for a CLEAN crawl test use
// currentPrice well below cap. Use Onion Rings: CSV cap ~650... also tight.
// The honest fixture: pass a dish whose catalog cap exceeds the step. Pizza Speciale
// (CSV ~3059) works: current 2000 -> 2100.
var t19eDish = cat.ById("Pizza Speciale")!;
var t19eSug = PricingController.Suggest(t19eDish, currentPrice: 2000, records: t19cRecords,
    costHundredths: 700, platableToday: 50, districtMedian: null, established: true);
Check("T19e thin coverage: crawl only (+5% on own data)",
    t19eSug != null && t19eSug.SuggestedPriceHundredths == 2100 && t19eSug.Diagnosis.Contains("no district benchmark"),
    t19eSug == null ? "no suggestion (unexpected)" : $"{t19eSug.Diagnosis}: 2000 -> {t19eSug.SuggestedPriceHundredths}");

// T19f — sticky-down: 2-day slump trims toward floor. Fixture: days 70/71 strong,
// days 72/73 slumped 20%+. Window is (71, 72, 73): last=73 slumped vs prior avg
// ((71+72)/2), prior=72 slumped vs 71. Price 3000, cost 2500 -> floor 2700.
// Cut: 3000 - 3000*500/10000 = 2850. Above floor -> 2850.
var t19fRecords = new List<SaleRecord> {
    new(71, 3000, 20, 22, 0, null),
    new(72, 3000, 14, 16, 0, null),   // 14 <= 20*0.8 = 16 -> slump day 1
    new(73, 3000, 10, 12, 0, null),   // 10 <= (20+14)/2*0.8 = 13.6 -> slump day 2
};
var t19fDish = cat.ById("Pizza Speciale")!;
var t19fSug = PricingController.Suggest(t19fDish, currentPrice: 3000, records: t19fRecords,
    costHundredths: 2500, platableToday: 50, districtMedian: null, established: true);
Check("T19f sticky-down: 2-day slump trims toward floor",
    t19fSug != null && t19fSug.SuggestedPriceHundredths == 2850 && t19fSug.Diagnosis.Contains("slump"),
    t19fSug == null ? "no suggestion (unexpected)" : $"{t19fSug.Diagnosis}: 3000 -> {t19fSug.SuggestedPriceHundredths}");

// T19g — single slow day is NOT a slump (stickiness): day 73 slow, day 72 strong.
var t19gRecords = new List<SaleRecord> {
    new(71, 3000, 20, 22, 0, null),
    new(72, 3000, 21, 23, 0, null),   // strong
    new(73, 3000, 10, 12, 0, null),   // one slow day: 10 <= (20+21)/2*0.8 = 16.4 -> today slumps...
};
// ...but prior day did NOT slump (21 >= 20*0.8) -> no 2-day trigger -> demand-bound path.
// No median -> crawl: 3000 -> 3150.
var t19gSug = PricingController.Suggest(t19fDish, currentPrice: 3000, records: t19gRecords,
    costHundredths: 2500, platableToday: 50, districtMedian: null, established: true);
Check("T19g single slow day: no cut; crawl continues",
    t19gSug != null && t19gSug.SuggestedPriceHundredths == 3150 && !t19gSug.Diagnosis.Contains("slump"),
    t19gSug == null ? "no suggestion (unexpected)" : $"{t19gSug.Diagnosis}: 3000 -> {t19gSug.SuggestedPriceHundredths}");
// T20 — MIN-VOLUME GATE (findings: Roaches x2 slump cuts, Roaches' 30.45 raise).
// Fixture semantics: clean = sold < cooked (kitchen plated headroom, demand honest).

// T20a — DOWN-GATE: textbook 2-day slump on a dish averaging 1.33/day -> NO cut.
var t20aRecords = new List<SaleRecord> {
    new(71, 3100, 2, 4, 0, null),    // clean: 2 of 4
    new(72, 3100, 1, 3, 0, null),    // clean: 1 - slump day 1 (1 <= 2*0.8=1.6)
    new(73, 3100, 1, 3, 0, null),    // clean: 1 - slump day 2 (1 <= 1.5*0.8=1.2)
};
var t20aDish = cat.ById("Poached Roaches")!;
var t20aSug = PricingController.Suggest(t20aDish, currentPrice: 2900, records: t20aRecords,
    costHundredths: 1500, platableToday: 50, districtMedian: null, established: true);
Check("T20a down-gate: sub-3/day dish gets no slump cut",
    t20aSug == null,
    t20aSug == null ? "clean avg 1.33 < gate 3 -> hold" : $"unexpected {t20aSug.Diagnosis}");

// T20b — UP-GATE: thin dish, no slump shape -> NO crawl either.
var t20bRecords = new List<SaleRecord> {
    new(71, 2900, 1, 3, 0, null),
    new(72, 2900, 2, 4, 0, null),
    new(73, 2900, 1, 3, 0, null),
};
var t20bSug = PricingController.Suggest(t20aDish, currentPrice: 2900, records: t20bRecords,
    costHundredths: 1500, platableToday: 50, districtMedian: null, established: true);
Check("T20b up-gate: sub-3/day dish gets no headroom crawl",
    t20bSug == null,
    t20bSug == null ? "clean avg 1.33 < gate 3 -> hold" : $"unexpected {t20bSug.Diagnosis}");

// T20c — ABOVE-GATE control: healthy volume, same slump PATTERN -> cut still fires,
// and quantizes DOWN (2600-130=2470 -> FloorToLims -> 2400).
var t20cRecords = new List<SaleRecord> {
    new(71, 2600, 15, 17, 0, null),
    new(72, 2600, 10, 12, 0, null),
    new(73, 2600, 9, 11, 0, null),
};
var t20cDish = cat.ById("Sweet Waffle")!;
var t20cSug = PricingController.Suggest(t20cDish, currentPrice: 2600, records: t20cRecords,
    costHundredths: 800, platableToday: 50, districtMedian: null, established: true);
Check("T20c above-gate: healthy-volume slump cuts, quantized down",
    t20cSug != null && t20cSug.SuggestedPriceHundredths == 2400 && t20cSug.Diagnosis.Contains("slump"),
    t20cSug == null ? "no suggestion (unexpected)" : $"{t20cSug.Diagnosis}: 2600 -> {t20cSug.SuggestedPriceHundredths}");

// T21 — WHOLE-LIM QUANTIZATION (operator finding: player UI snaps to .00).
// Cap safety discipline: cap the crawl explicitly by passing currentPrice low enough
// that +5% always clears it; the CAP lives in the fixture's chosen dish+price, verified
// in the derivation, not estimated from generator math.

// T21a — crawl-up rounds UP to the next whole lim: 2945 + 5% = 3092.25 -> 3092 raw
// -> CeilingToLims -> 3100. Sweet Waffle CSV cap (Dessert 1.8 x inputs ~800) = ~1440...
// TOO LOW. Use a high-cap dish: Pizza Speciale (CSV 3400). current 2000 + 5% = 2100 raw
// -> CeilingToLims(2100) = 2100 (already whole) -> 2100. To prove ROUNDING-UP (not
// pass-through), we need a raw step that lands mid-lim: current 2050? Prices are
// hundredths-ints, so use 2050 = 20.50 lims: 2050+102.5... crawlStepBps=500 ->
// 2050 + 102 = 2152 -> CeilingToLims -> 2200.
var t21aRecords = new List<SaleRecord> {
    new(71, 2050, 6, 8, 0, null),
    new(72, 2050, 5, 7, 0, null),
    new(73, 2050, 5, 7, 0, null),    // clean avg 5.33 >= 3; no slump (5 > 4.4)
};
var t21aDish = cat.ById("Pizza Speciale")!;   // CSV cap 3400 -> min(2152, 3400) = 2152
var t21aSug = PricingController.Suggest(t21aDish, currentPrice: 2050, records: t21aRecords,
    costHundredths: 1000, platableToday: 50, districtMedian: null, established: true);
Check("T21a crawl-up quantizes UP: 20.50 -> 22.00 (raw 21.52 ceilings to 22.00)",
    t21aSug != null && t21aSug.SuggestedPriceHundredths == 2200,
    t21aSug == null ? "no suggestion (unexpected)" : $"got {t21aSug.SuggestedPriceHundredths}");

// T21c — same fixture, quantization OFF: raw 2152 preserved.
var t21cSug = PricingController.Suggest(t21aDish, currentPrice: 2050, records: t21aRecords,
    costHundredths: 1000, platableToday: 50, districtMedian: null, established: true,
    roundToWholeLims: false);
Check("T21c quantization off: raw hundredths preserved (2152)",
    t21cSug != null && t21cSug.SuggestedPriceHundredths == 2152,
    t21cSug == null ? "no suggestion (unexpected)" : $"got {t21cSug.SuggestedPriceHundredths}");

// T21d — cheap-dish crawl, anti-stall: Beer cap 350 (CSV-verified). current 300,
// raw step 315 -> CeilingToLims -> 400, cap clips to 350: 300 -> 350 = one lim, at the ceiling.
var t21dRecords = new List<SaleRecord> {
    new(71, 300, 10, 12, 0, null),
    new(72, 300, 11, 13, 0, null),
    new(73, 300, 12, 14, 0, null),
};
var t21dDish = cat.ById("Beer")!;   // cap 350, verified from dishdata.csv
var t21dSug = PricingController.Suggest(t21dDish, currentPrice: 300, records: t21dRecords,
    costHundredths: 200, platableToday: 50, districtMedian: null, established: true);
Check("T21d cheap-dish crawl: 3.00 -> 3.50 (raw 315 ceilings to 400, cap clips to 350)",
    t21dSug != null && t21dSug.SuggestedPriceHundredths == 350,
    t21dSug == null ? "no suggestion (unexpected)" : $"got {t21dSug.SuggestedPriceHundredths}");
return failed;