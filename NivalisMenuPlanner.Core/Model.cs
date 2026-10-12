using System.Collections.Generic;

namespace NivalisMenuPlanner.Core;

public enum DishType { Unknown, Drink, Soup, Meat, Fish, Seafood, Sandwich, Salad, Snack, Dessert, Pizza }

public sealed record Ingredient(string Item, int Amount);

public sealed record DishInfo(
    string Id, string Name, DishType Type, int PriceCapHundredths,
    IReadOnlyList<Ingredient> Inputs,
    float Hipsters, float Workers, float Elite);

public sealed record StaffMember(string Name, decimal HourlyRate, int MinutesRemainingOnShift);

public sealed record VenueState(
    string Id, string Name, string District, int MaxSlots,
    int LocalExclusivity,      // 1–5 (scale still to VERIFY via DevBridge dump — assumed 1–5)
    int ManagerGrade,          // 0 = no manager, else 1–10
    IReadOnlyDictionary<string, int> Stock,
    IReadOnlyDictionary<string, int> BuyPrice,   // shelf price in hundredths of a lim
    IReadOnlyList<(string Group, float Weight)> CustomerMix,
    IReadOnlyList<string> KnownDishIds,
    int? StorageCapacity = null);   // total units the venue can hold (normal + refrigerated), null = unlimited

/// <summary>Operator rulings, config-exposed per the session ruling: every decision
/// threshold is a dial; only data-plumbing (store schema, retention) stays fixed.</summary>
public sealed record MenuRules(
    int MaxSlots,
    bool PreferBeverage, bool PreferEntree, bool PreferDessert,
    IReadOnlyList<DishType> EntreeTypes, int MaxPerType,
    float ChurnPenalty, float StockDiscount,
    float RoleBonus,
    int MinErrandValueHundredths = 500,
    int MinVolumeGate = 3);   // dishes averaging below this/day get NO price moves, either lever (5d: the Roaches/Waffle findings)

public sealed record MenuState(IReadOnlyList<string> Yesterday, float DemandScale);

public sealed record DishLine(DishInfo Dish, int PriceHundredths, int CostHundredths, double ExpectedOrders)
{
    public decimal Margin => (PriceHundredths - CostHundredths) / 100m;
}

public sealed record ShoppingItem(
    string Item, int Needed, int UnitPriceHundredths,
    int ProtectedHundredths = 0);   // profit-at-risk this entry covers; urgency = protected / cost

public sealed record MenuSuggestion(
    IReadOnlyList<DishLine> Dishes, decimal EstProfitPerDay,
    IReadOnlyList<ShoppingItem> Shopping, IReadOnlyList<string> Warnings);