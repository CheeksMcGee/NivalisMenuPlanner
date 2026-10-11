# ModKit gaps hit in practice
Format: what we tried / what's missing / why the workaround falls short

1. Menu read/write (v0.3 auto-apply) — READ exists: Venues.MenuOf(area) (Recipe, Dish, Price,
   Ingredients per slot). WRITE does not — still the one upstream ask for auto-apply.
2. Managers.Of(area) -> name/grade — not in the kit API (verified against NivalisModKit.xml).
   Workaround: ManagerGrade = 0 (player mode).
3. Local Exclusivity readout — not in the kit API. Workaround: manual config per venue.
4. Staff wages/shifts — RESOLVED by kit API: Venues.StaffOf(area) returns StaffMember with
   Wage (hundredths/hour) and HoursLeftToday; plugin feeds Payroll escrow with live data.
5. PriceMult curve (110% g1 -> 80% g10) — not verified in-game. Workaround: flagged until
   checked vs purchase receipts (StaffPaid / PlayerBought events can verify).
6. Shared Pantry — RESOLVED out of scope: venue storage only; non-interference by design.
7. Dish sell price / price cap — absent from `content dump`. IMPROVEMENT PATH FOUND:
   Venues.MenuOf(area).Price exposes real menu prices, and Venues.All includes NPC venues —
   harvest at GameReady to replace estimated caps. Workaround until then: cost x type factor
   via tools/make_dishdata.ps1.
8. Per-dish customer-group satisfaction — absent from dump. IMPROVEMENT PATHS FOUND:
   Venues.PopularityOf(area) (per group) and Venues.ReceiptsOf(area, day) (actual per-dish
   sales) for v0.2 demand calibration. Workaround: generator heuristics + estimated district mixes.
9. Recipe inventory — dump shows 192 (156 venue + 36 playerOnly); community site's "193 venue"
   disproven; dump is truth. Regenerate after every game patch.
10. Kvass asset collision — two assets named Kvass (buyable ingredient vs crafted drink); recipe
    inputs are name-only. Generator prefers the buyable item for costing; flagged in warnings.
11. Craft-chain inputs — RESOLVED in Core: crafted-only ingredients are acquirable via their
    own recipes (depth-limited, unlock-checked) — T11.
12. Vendor stockMultiplier — v0.1 uses cheapest vendor only; per-vendor stock depth deferred to
    v0.2 (Economy.Stock(vendor, item) provides it).
13. Purchasing pipeline — CONFIRMED in kit API: Purchasing.OrderQuantity + VendorOrdering are
    exactly the v0.3 'managers buy to the plan' hooks. No upstream ask needed for purchasing.
14. CSV keys are ASSET names (Items.NameOf's format), display names only for UI — enforced by
    the generator since the curly-apostrophe/Plural-name mismatch would silently break lookups.
15. receipt.Count semantics — RESOLVED by rig verification, day 71: records
    reconcile EXACTLY with end-of-day gross at both venues (Ramen Noir
    146,100 hundredths = screen 1461 lims; Boat Hangar 159,900 = 1599).
    receipt.Count = units sold; SaleRecord.Sold x Price = the game's gross.
    Verification mechanism: venue-total reconciliation (screen shows no
    per-dish itemization; total-level works and is sufficient).
16. Sold > Cooked days — operator hypothesis (receipts may tally at order
    time, not plate time) fits day-66/68 data better than replacement-cook
    undercount; adjudication test = subscribe SaleMade (payment-at-departure
    per kit docs) with day/hour stamp, compare per-dish totals vs ReceiptsOf.
    Until then: WalkedOut valid only when Sold <= Cooked; Sold > Cooked days
    had at least (Sold - Cooked) unseen walk-outs.
17. Dishwasher observation — abandoned plates are destroyed (ingredients +
    prep labor lost), not stored; walk-outs cost more than lost revenue.
    Cooked counter may miss replacement cooks (tally semantics per #16).
18. Screen "out" (expenses) side — noted as future escrow/shopping-list
    verification, same total-reconciliation trick, other side of the ledger.
19. PopularityOf group-shape — still deferred; the SaleMade test session can
    also probe /venues bridge page for the group structure to wire the
    SaleRecord popularity field.