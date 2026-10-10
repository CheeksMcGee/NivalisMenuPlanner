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