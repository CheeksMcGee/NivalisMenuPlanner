using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisMenuPlanner.Core;

public static class Planner
{
    // How deep crafted-input chains may go when a recipe output is used as an ingredient
    // (Beer <- Wheat is depth 1). Deeper chains are treated as unavailable.
    const int MaxCraftDepth = 3;

    public static MenuSuggestion Plan(DishCatalog cat, VenueState v, MenuRules r, MenuState st)
    {
        var knownCatalog = v.KnownDishIds
            .Select(cat.ById).Where(d => d != null).Select(d => d!)
            .ToList();
        var known = knownCatalog
            .Where(d => Sourceable(d, v, cat))
            .ToList();

        var warnings = new List<string>();
        if (knownCatalog.Count > known.Count)
            warnings.Add($"{knownCatalog.Count - known.Count} known dish(es) skipped — inputs not stocked, sold, or craftable.");
        var mix = EffectiveMix(v);

        // ESCROW LEDGER (Step 4a): ingredient units claimed by dishes already placed.
        // Each placed dish commits ceil(its ACTUAL orders x amount) per input; the next
        // dish scores against AvailableStock (stock - committed). Reserve-before-spend.
        var committed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int AvailableOf(string item) =>
            AvailableStock(v, item, committed);

        // CAPACITY-ADJUSTED SCORING (Option B): a dish cannot earn more than the
        // available stock can plate - held stock minus what earlier dishes claimed.
        int ServingsFrom(string item, int amount) =>
            amount <= 0 ? int.MaxValue : AvailableOf(item) / amount;

        int ServingsPossibleFromLedger(DishInfo d, DishCatalog catRef, int depth = 0)
        {
            int servings = int.MaxValue;
            foreach (var (item, amount) in d.Inputs)
            {
                int byThisInput = ServingsFrom(item, amount);

                if (byThisInput == 0 && depth < MaxCraftDepth)
                {
                    DishInfo? sub = catRef.ById(item);
                    if (sub != null && v.KnownDishIds.Contains(sub.Id))
                    {
                        int subServings = ServingsPossibleFromLedger(sub, catRef, depth + 1);
                        if (subServings == 0) return 0;
                        byThisInput = subServings / amount;
                    }
                }
                servings = Math.Min(servings, byThisInput);
                if (servings == 0) return 0;
            }
            return servings == int.MaxValue ? 0 : servings;
        }

        double OrdersFor(DishInfo d)
        {
            double raw = st.DemandScale * Mix(mix, d);
            int servable = ServingsPossibleFromLedger(d, cat);
            return Math.Min(raw, servable);
        }

        double BaseScore(DishInfo d)
        {
            var cost = EffCost(d, v, r, cat);
            var price = d.PriceCapHundredths;
            var orders = OrdersFor(d);
            var churn = st.Yesterday.Contains(d.Id) ? 0.0 : r.ChurnPenalty;
            var gate = ExclusivityGate(d, v, cat);
            return orders * (price - cost) * gate * (1.0 - churn);
        }

        int NewlyCovered(DishInfo d, List<DishInfo> menu)
        {
            int n = 0;
            if (r.PreferBeverage && d.Type == DishType.Drink && menu.Count(x => x.Type == DishType.Drink) == 0) n++;
            if (r.PreferEntree && r.EntreeTypes.Contains(d.Type) && menu.Count(x => r.EntreeTypes.Contains(x.Type)) == 0) n++;
            if (r.PreferDessert && d.Type == DishType.Dessert && menu.Count(x => x.Type == DishType.Dessert) == 0) n++;
            return n;
        }

        double EffectiveScore(DishInfo d, List<DishInfo> menu) =>
            BaseScore(d) * (1.0 + r.RoleBonus * NewlyCovered(d, menu));

        int maxSlots = Math.Min(r.MaxSlots, Math.Max(v.MaxSlots, 1));
        var menu = new List<DishInfo>();
        var perType = new Dictionary<DishType, int>();

        int cap = r.MaxPerType;
        while (menu.Count < maxSlots)
        {
            DishInfo? best = null;
            double bestScore = 0;
            foreach (var d in known)
            {
                if (menu.Contains(d) || BaseScore(d) <= 0) continue;
                if (perType.GetValueOrDefault(d.Type) >= cap) continue;
                double s = EffectiveScore(d, menu);
                if (s > bestScore) { bestScore = s; best = d; }
            }
            if (best != null)
            {
                menu.Add(best);
                perType[best.Type] = perType.GetValueOrDefault(best.Type) + 1;
                CommitInputs(best, OrdersFor(best), committed, v, cat, 0);
                continue;
            }
            cap++;
            if (cap > known.Count) break;
        }

        if (known.Count == 0)
            warnings.Add("No unlocked, sourceable recipes for this venue.");
        else if (menu.Count == 0)
            warnings.Add("No unlocked dish clears the profit bar (every expected contribution <= 0).");
        else if (menu.Count < maxSlots)
            warnings.Add($"Menu holds {menu.Count} of {maxSlots} slots — nothing else clears the profit bar. Unlock more recipes to fill it.");

        // improvement passes: swaps re-commit the ledger in simulation
        double MenuScore(List<DishInfo> m)
        {
            var simLedger = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            double total = 0;
            bool bev = false, ent = false, des = false;
            foreach (var d in m)
            {
                var orders = Math.Min(st.DemandScale * Mix(mix, d),
                    SimulatedServings(d, simLedger));
                double s = orders * (d.PriceCapHundredths - EffCost(d, v, r, cat))
                    * ExclusivityGate(d, v, cat);
                if (!st.Yesterday.Contains(d.Id)) s *= (1.0 - r.ChurnPenalty);
                if (r.PreferBeverage && d.Type == DishType.Drink && !bev) { s *= 1.0 + r.RoleBonus; bev = true; }
                if (r.PreferEntree && r.EntreeTypes.Contains(d.Type) && !ent) { s *= 1.0 + r.RoleBonus; ent = true; }
                if (r.PreferDessert && d.Type == DishType.Dessert && !des) { s *= 1.0 + r.RoleBonus; des = true; }
                CommitInputs(d, orders, simLedger, v, cat, 0);
                total += s;
            }
            return total;
        }

        if (menu.Count > 0)
        {
            int effectiveCap = Math.Max(r.MaxPerType,
                menu.GroupBy(d => d.Type).Select(g => g.Count()).DefaultIfEmpty(0).Max());
            for (int pass = 0; pass < 3; pass++)
            {
                bool improved = false;
                double current = MenuScore(menu);
                foreach (var cand in known.Where(d => !menu.Contains(d) && BaseScore(d) > 0).ToList())
                {
                    var victim = menu.OrderBy(BaseScore).First();
                    int idx = menu.IndexOf(victim);
                    menu[idx] = cand;
                    if (MenuScore(menu) > current * 1.001 && TypeOk(menu, maxSlots, effectiveCap))
                    {
                        perType[victim.Type]--;
                        perType[cand.Type] = perType.GetValueOrDefault(cand.Type) + 1;
                        improved = true;
                        break;
                    }
                    menu[idx] = victim;
                }
                if (!improved) break;
            }
        }

        // FINAL LEDGER + FINAL ORDERS: rebuild both in menu order. finalOrders carries
        // each dish's LEDGER-CAPPED order count into BuildResult - the plan's honest
        // promise. The T17d lesson: recomputing orders from raw stock in the reporting
        // layer ignores the escrow and overstates what the menu can serve.
        var finalOrders = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (menu.Count > 0)
        {
            committed.Clear();
            foreach (var d in menu)
            {
                var o = OrdersFor(d);
                finalOrders[d.Id] = o;
                CommitInputs(d, o, committed, v, cat, 0);
            }
        }

        if (perType.Values.Any(n => n > r.MaxPerType))
            warnings.Add($"Variety cap relaxed to {perType.Values.Max()} per type — few recipe types unlocked; a balanced menu needs more.");

        if (r.PreferBeverage && menu.All(d => d.Type != DishType.Drink))
            warnings.Add("No beverage made the cut — none beat the alternatives (or cleared the profit bar).");
        if (r.PreferEntree && menu.All(d => !r.EntreeTypes.Contains(d.Type)))
            warnings.Add("No entrée made the cut — none beat the alternatives (or cleared the profit bar).");
        if (r.PreferDessert && menu.All(d => d.Type != DishType.Dessert))
            warnings.Add("No dessert made the cut — none beat the alternatives (or cleared the profit bar).");

        menu = menu.OrderByDescending(BaseScore).ToList();
        return BuildResult(menu, v, r, st, mix, warnings, cat, committed, finalOrders);
    }

    // grade -> fraction of shelf price the manager pays (verified: 110% g1 -> 80% g10; player = 100%)
    public static decimal PriceMult(int grade) =>
        grade <= 0 ? 1.00m : grade >= 10 ? 0.80m : 1.10m - 0.30m * (grade - 1) / 9m;

    // CAPACITY GUARD (raw-stock form; kept public for the harness and GameAdapter).
    // Plan() uses its ledger-aware sibling; this remains the honest
    // "what does the PANTRY alone plate" answer.
    public static int ServingsPossible(DishInfo d, VenueState v, DishCatalog cat, int depth = 0)
    {
        int servings = int.MaxValue;
        foreach (var (item, amount) in d.Inputs)
        {
            v.Stock.TryGetValue(item, out var stock);
            int byThisInput = amount <= 0 ? int.MaxValue : stock / amount;

            if (byThisInput == 0 && depth < MaxCraftDepth)
            {
                DishInfo? sub = cat.ById(item);
                if (sub != null && v.KnownDishIds.Contains(sub.Id))
                {
                    int subServings = ServingsPossible(sub, v, cat, depth + 1);
                    if (subServings == 0) return 0;
                    byThisInput = subServings / amount;
                }
            }
            servings = Math.Min(servings, byThisInput);
            if (servings == 0) return 0;
        }
        return servings == int.MaxValue ? 0 : servings;
    }

    // ESCROW VIEW: stock with committed amounts subtracted (public, Step 4's lens)
    public static int AvailableStock(VenueState v, string item, Dictionary<string, int>? committed)
    {
        v.Stock.TryGetValue(item, out var stock);
        var held = committed?.GetValueOrDefault(item) ?? 0;
        return Math.Max(0, stock - held);
    }

    // ESCROW COMMIT (private workhorse): a placed dish claims its ACTUAL order count
    // x per-input amount. Craft-chain inputs commit at their base ingredients.
    static void CommitInputs(DishInfo d, double orders, Dictionary<string, int> committed,
        VenueState v, DishCatalog cat, int depth)
    {
        int units = (int)Math.Ceiling(orders);
        foreach (var (item, amount) in d.Inputs)
        {
            DishInfo? sub = depth < MaxCraftDepth && !v.BuyPrice.ContainsKey(item) ? cat.ById(item) : null;
            if (sub != null)
            {
                CommitInputs(sub, orders, committed, v, cat, depth + 1);
            }
            else
            {
                committed[item] = committed.GetValueOrDefault(item) + units * amount;
            }
        }
    }

    // RAW-DEMAND ACCRUAL: the shopping list's source of truth. For every menu dish,
    // accrue ceil(RAW orders) x amount per input, chain-expanded to base ingredients.
    // THEOREM (T11, three predictions in the making): under Option B + the escrow,
    // capped claims are provably <= stock, so a list built from CAPPED orders is always
    // empty. The honest list is the RAW demand gap: buy = totalRawNeed - stock, so
    // tomorrow's plan serves full demand instead of capping. The gap warning and this
    // list are the same promise.
    static void Accrue(DishInfo d, int units, Dictionary<string, int> needs,
        VenueState v, DishCatalog cat, int depth)
    {
        foreach (var (item, amount) in d.Inputs)
        {
            DishInfo? sub = depth < MaxCraftDepth && !v.BuyPrice.ContainsKey(item) ? cat.ById(item) : null;
            if (sub != null && v.KnownDishIds.Contains(sub.Id))
                Accrue(sub, units, needs, v, cat, depth + 1);
            else
                needs[item] = needs.GetValueOrDefault(item) + units * amount;
        }
    }

    // Simulated servings for swap evaluation: capacity as if simLedger were the real one
    static int SimulatedServings(DishInfo d, Dictionary<string, int> simLedger, int depth = 0)
    {
        int servings = int.MaxValue;
        foreach (var (item, amount) in d.Inputs)
        {
            simLedger.TryGetValue(item, out var avail);
            int byThisInput = amount <= 0 ? int.MaxValue : avail / amount;
            servings = Math.Min(servings, byThisInput);
            if (servings == 0) return 0;
        }
        return servings == int.MaxValue ? 0 : servings;
    }

    // An input can come from: pantry stock, a vendor, or a known recipe that crafts it
    static bool Sourceable(DishInfo d, VenueState v, DishCatalog cat, int depth = 0)
    {
        foreach (var (item, amount) in d.Inputs)
        {
            if (v.Stock.TryGetValue(item, out var s) && s >= amount) continue;
            if (v.BuyPrice.ContainsKey(item)) continue;
            DishInfo? sub = depth < MaxCraftDepth ? cat.ById(item) : null;
            if (sub != null
                && v.KnownDishIds.Contains(sub.Id)
                && Sourceable(sub, v, cat, depth + 1)) continue;
            return false;
        }
        return true;
    }

    static int EffCost(DishInfo d, VenueState v, MenuRules r, DishCatalog cat)
    {
        decimal cost = 0m;
        foreach (var (item, amount) in d.Inputs)
        {
            var price = InputUnitPrice(item, v, cat, 0);
            v.Stock.TryGetValue(item, out var stock);
            cost += stock >= amount
                ? price * amount * (decimal)r.StockDiscount   // sunk stock: use it or lose it
                : price * amount;                             // acquire at the effective price
        }
        return (int)cost;
    }

    static decimal InputUnitPrice(string item, VenueState v, DishCatalog? cat, int depth)
    {
        if (v.BuyPrice.TryGetValue(item, out var shelf) && shelf > 0)
            return Math.Round(shelf * PriceMult(v.ManagerGrade));
        if (depth < MaxCraftDepth && cat != null)
        {
            DishInfo? sub = cat.ById(item);
            if (sub != null)
            {
                decimal craft = 0m;
                foreach (var (subItem, subAmount) in sub.Inputs)
                    craft += InputUnitPrice(subItem, v, cat, depth + 1) * subAmount;
                return craft;
            }
        }
        return 0m;
    }

    public static decimal RawCostLims(DishInfo d, VenueState v, DishCatalog? cat = null)
    {
        decimal cost = 0m;
        foreach (var (item, amount) in d.Inputs)
            cost += InputUnitPrice(item, v, cat, 0) * amount;
        return cost / 100m;
    }

    static double ExclusivityGate(DishInfo d, VenueState v, DishCatalog cat)
    {
        decimal cap = v.LocalExclusivity switch { <= 2 => 6m, 3 => 9m, 4 => 12m, _ => 15m };
        decimal over = RawCostLims(d, v, cat) - cap;
        return over <= 0 ? 1.0 : Math.Max(0.25, 1.0 - (double)(over / cap));
    }

    static IReadOnlyList<(string Group, float Weight)> EffectiveMix(VenueState v) =>
        v.LocalExclusivity <= 2 ? ShiftToward(v.CustomerMix, "Workers")
        : v.LocalExclusivity >= 4 ? ShiftToward(v.CustomerMix, "Elite")
        : v.CustomerMix;

    static IReadOnlyList<(string Group, float Weight)> ShiftToward(
        IReadOnlyList<(string Group, float Weight)> mix, string group)
    {
        var list = mix.ToList();
        int i = list.FindIndex(x => x.Group == group);
        if (i < 0) { list.Add((group, 0f)); i = list.Count - 1; }
        list[i] = (list[i].Group, list[i].Weight + 0.3f);
        var total = list.Sum(x => x.Weight);
        return list.Select(x => (x.Group, x.Weight / total)).ToList();
    }

    static float Mix(IReadOnlyList<(string Group, float Weight)> mix, DishInfo d)
    {
        float w = 0f;
        foreach (var (group, weight) in mix)
        {
            float score = group switch
            {
                "Hipsters" => d.Hipsters,
                "Workers" => d.Workers,
                "Elite" => d.Elite,
                _ => 60f
            };
            w += weight * (score / 100f);
        }
        return w;
    }

    static bool TypeOk(List<DishInfo> menu, int maxSlots, int cap) =>
        menu.Count <= maxSlots
        && menu.GroupBy(d => d.Type).All(g => g.Count() <= cap);

    static MenuSuggestion BuildResult(List<DishInfo> menu, VenueState v, MenuRules r, MenuState st,
        IReadOnlyList<(string Group, float Weight)> mix, List<string> warnings, DishCatalog cat,
        Dictionary<string, int> committed, Dictionary<string, double> finalOrders)
    {
        var lines = new List<DishLine>();
        var rawNeeds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        decimal profit = 0m;
        foreach (var d in menu)
        {
            var cost = EffCost(d, v, r, cat);
            double rawOrders = st.DemandScale * Mix(mix, d);
            // the plan's honest promise: ledger-capped orders from the rebuild
            var orders = finalOrders.GetValueOrDefault(d.Id, Math.Min(rawOrders, ServingsPossible(d, v, cat)));

            if (rawOrders > orders + 0.01)
                warnings.Add($"{d.Name}: pantry serves {(int)Math.Ceiling(orders)} of expected {(int)Math.Ceiling(rawOrders)} — the gap is on your shopping list.");

            profit += (decimal)orders * (d.PriceCapHundredths - cost) / 100m;
            lines.Add(new DishLine(d, d.PriceCapHundredths, cost, orders));
            Accrue(d, (int)Math.Ceiling(rawOrders), rawNeeds, v, cat, 0);
        }

        // SHOPPING LIST = the raw-demand gap, aggregated across the menu:
        // buy enough that tomorrow's plan serves full demand instead of capping.
        var shopping = new Dictionary<string, int>();
        foreach (var kv in rawNeeds)
        {
            v.Stock.TryGetValue(kv.Key, out var stock);
            int gap = kv.Value - stock;
            if (gap > 0) AddNeed(shopping, kv.Key, gap, v, cat, 0);
        }
        var list = shopping.OrderBy(kv => kv.Key)   // alphabetical until sub-step 4b (urgency)
            .Select(kv => new ShoppingItem(kv.Key, kv.Value, v.BuyPrice.GetValueOrDefault(kv.Key)))
            .ToList();
        return new MenuSuggestion(lines, profit, list, warnings);
    }

    static void AddNeed(Dictionary<string, int> shopping, string item, int need, VenueState v, DishCatalog cat, int depth)
    {
        if (need <= 0) return;
        DishInfo? sub = depth < MaxCraftDepth && !v.BuyPrice.ContainsKey(item) ? cat.ById(item) : null;
        if (sub == null)
        {
            shopping[item] = shopping.GetValueOrDefault(item) + need;
            return;
        }
        foreach (var (subItem, subAmount) in sub.Inputs)
        {
            v.Stock.TryGetValue(subItem, out var subStock);
            AddNeed(shopping, subItem, need * subAmount - subStock, v, cat, depth + 1);
        }
    }
}