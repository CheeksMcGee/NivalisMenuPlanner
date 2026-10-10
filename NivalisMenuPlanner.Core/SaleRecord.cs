using System;

namespace NivalisMenuPlanner.Core;

/// <summary>
/// One dish's commercial performance at one venue on one game day.
/// Harvested at DayEnded; the training set for demand fitting (v0.2.1),
/// the input for the pricing controller, and the service-quality signal.
/// </summary>
/// <param name="Day">Game day (GameTime.Day at harvest).</param>
/// <param name="Price">Menu price actually charged that day, hundredths of a lim.</param>
/// <param name="Sold">Units sold (paid receipts).</param>
/// <param name="Cooked">Units cooked (finished plates, whether sold or not).</param>
/// <param name="WalkedOut">Derived at harvest: Cooked - Sold (abandoned plates —
/// a service signal, NOT a price signal; see design notes in GAPS/pricing spec).</param>
/// <param name="Popularity">Venue popularity with the dish's best customer group,
/// 0..1 at day end, or null when the game reports none.</param>
public sealed record SaleRecord(
    int Day,
    int Price,
    int Sold,
    int Cooked,
    int WalkedOut,
    float? Popularity)
{
    /// <summary>Leftover ratio: WalkedOut / max(Cooked, 1). 0 when everything sold.</summary>
    public float AbandonRate => Cooked <= 0 ? 0f : (float)WalkedOut / Cooked;

    /// <summary>Sold out completely: demand met or exceeded supply.</summary>
    public bool SoldOut => Cooked > 0 && Sold >= Cooked;
}