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
    int LocalExclusivity,
    int ManagerGrade,
    IReadOnlyDictionary<string, int> Stock,
    IReadOnlyDictionary<string, int> BuyPrice,
    IReadOnlyList<(string Group, float Weight)> CustomerMix,
    IReadOnlyList<string> KnownDishIds);

public sealed record MenuRules(
    int MaxSlots,
    bool PreferBeverage, bool PreferEntree, bool PreferDessert,
    IReadOnlyList<DishType> EntreeTypes, int MaxPerType,
    float ChurnPenalty, float StockDiscount,
    float RoleBonus);

public sealed record MenuState(IReadOnlyList<string> Yesterday, float DemandScale);

public sealed record DishLine(DishInfo Dish, int PriceHundredths, int CostHundredths, double ExpectedOrders)
{
    public decimal Margin => (PriceHundredths - CostHundredths) / 100m;
}

public sealed record ShoppingItem(string Item, int Needed, int UnitPriceHundredths);

public sealed record MenuSuggestion(
    IReadOnlyList<DishLine> Dishes, decimal EstProfitPerDay,
    IReadOnlyList<ShoppingItem> Shopping, IReadOnlyList<string> Warnings);