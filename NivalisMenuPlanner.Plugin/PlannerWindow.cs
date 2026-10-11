using System;
using System.Linq;
using NivalisModKit;

namespace NivalisMenuPlanner;

public static class PlannerWindow
{
    static KitWindow? _window;
    static int _venueIdx;
    static Plugin.Rotation[] _last = Array.Empty<Plugin.Rotation>();

    public static void Toggle(Plugin.Rotation[] rotations)
    {
        if (_window != null && _window.IsOpen) { _window.Hide(); return; }
        Show(rotations);
    }

    public static void Show(Plugin.Rotation[] rotations)
    {
        _last = rotations;
        if (_venueIdx >= _last.Length) _venueIdx = 0;
        if (_window == null)
        {
            _window = Ui.CreateWindow("Menu Planner", WindowStyle.Panel);
            if (_window == null) { Plugin.L.LogWarning("Ui.CreateWindow returned null — game UI not ready; try in gameplay."); return; }
            _window.AddFooterButton("Replan now", () => { Plugin.ReplanAll(); _last = Plugin.Current(); Build(_window!); });
            _window.AddFooterButton("Close", _window.Hide);
        }
        Build(_window);
        _window.Show();
    }

    public static void BuildPage(KitWindow w, Plugin.Rotation[] rotations)
    {
        _last = rotations;
        if (_venueIdx >= _last.Length) _venueIdx = 0;
        Build(w);
    }

    public static void DestroyWindow()
    {
        _window?.Close();
        _window = null;
        _last = Array.Empty<Plugin.Rotation>();
    }

    static void Build(KitWindow w)
    {
        w.Clear();
        if (_last.Length == 0)
        {
            w.AddText(Plugin.CatalogLoaded
                ? "No player venues yet (or nothing planned). Plans refresh at day start — or press Replan after loading a save."
                : "dishdata.csv was not found next to the plugin DLL.");
            return;
        }
        var r = _last[_venueIdx];
        w.AddText($"{r.Title} — est. {r.Suggestion.EstProfitPerDay:0.00} lims/day");
        w.AddChoice("Venue", _last.Select(x => x.Title).ToArray(), _venueIdx, i => { _venueIdx = i; Build(w); });

        w.AddHeader("Menu (suggested — apply in your venue screen)");
        foreach (var line in r.Suggestion.Dishes)
            w.AddText($"{line.Dish.Name} — margin {line.Margin:0.00} lims, ~{line.ExpectedOrders:0.0} orders");
        foreach (var warn in r.Suggestion.Warnings)
            w.AddText("! " + warn);

        // ---- PRICES section (5c): the doctrine's suggestions, advisory ----
        if (r.Suggestions.Count > 0)
        {
            w.AddHeader("Prices (suggestions — nothing changes without you)");
            w.AddText(r.Established
                ? "Established venue. Catch-up-to-market first, then verified crawls."
                : "Entry posture (reputation still growing): compete on price, bump in kind later.");
            foreach (var s in r.Suggestions)
                w.AddText($"{s.DishId}: {s.CurrentPriceHundredths / 100m:0.00} -> {s.SuggestedPriceHundredths / 100m:0.00} — {s.Rationale}");
        }

        w.AddHeader("Shopping (covers expected orders; venue storage only)");
        foreach (var s in r.Suggestion.Shopping.Take(12))
            w.AddText($"{s.Item} x{s.Needed}");
        if (r.Suggestion.Shopping.Count > 12)
            w.AddText($"... {r.Suggestion.Shopping.Count - 12} more");

        var pay = Plugin.PayrollSummary();
        if (pay != null) { w.AddHeader("Payroll"); w.AddText(pay); }
    }
}