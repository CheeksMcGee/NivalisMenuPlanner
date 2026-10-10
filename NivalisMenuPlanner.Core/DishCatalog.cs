using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NivalisMenuPlanner.Core;

public sealed class DishCatalog
{
    readonly Dictionary<string, DishInfo> _byId = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _ingredientNames = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _ingredientOrder = new();

    // Customer-group weights per district: (Workers, Hipsters, Elite). Keys are the canon
    // district names from the vendors dump ('Spire', not 'The Spire'). Values are ESTIMATES
    // from community district-fit research — replace when a source exposes real mixes.
    static readonly Dictionary<string, float[]> DistrictMix = new()
    {
        ["Spire"] = new[] { .15f, .30f, .55f },
        ["Calypso Island"] = new[] { .20f, .30f, .50f },
        ["Skyhigh Gardens"] = new[] { .15f, .50f, .35f },
        ["Helix Monumental"] = new[] { .30f, .40f, .30f },
        ["Seaside Boardwalk"] = new[] { .35f, .35f, .30f },
        ["Mountain Town"] = new[] { .40f, .35f, .25f },
        ["Space Port"] = new[] { .40f, .33f, .27f },
        ["Central Canyon"] = new[] { .45f, .32f, .23f },
        ["Meridian Market"] = new[] { .45f, .35f, .20f },
        ["Metro Hub"] = new[] { .45f, .35f, .20f },
        ["Hive Mall"] = new[] { .50f, .30f, .20f },
        ["Eastern Residential"] = new[] { .50f, .30f, .20f },
        ["Docks"] = new[] { .55f, .30f, .15f },
        ["Lowtown"] = new[] { .60f, .28f, .12f },
        ["Industrial District"] = new[] { .65f, .25f, .10f },
        ["Sewers"] = new[] { .70f, .20f, .10f },
        ["Stacks"] = new[] { .70f, .20f, .10f },
        ["Oil Rig"] = new[] { .75f, .15f, .10f },
    };

    public int Count => _byId.Count;
    public IReadOnlyList<string> Ids => _byId.Keys.ToArray();

    /// <summary>Every ingredient name the catalog knows (asset names, deduplicated).</summary>
    public IReadOnlyList<string> IngredientNames => _ingredientOrder;

    // CSV: id,name,type,cap,hip,wrk,elt,ingredients — ingredients separated by ';' (never commas)
    public static DishCatalog LoadCsv(string path)
    {
        var cat = new DishCatalog();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = line.Split(',');
            Enum.TryParse(f[2], ignoreCase: true, out DishType type);
            var inputs = f[7].Split(';', StringSplitOptions.RemoveEmptyEntries)
                             .Select(i => new Ingredient(i.Trim(), 1))
                             .ToList();
            foreach (var i in inputs)
                if (cat._ingredientNames.Add(i.Item)) cat._ingredientOrder.Add(i.Item);
            cat._byId[f[0].Trim()] = new DishInfo(f[0].Trim(), f[1].Trim(), type,
                int.Parse(f[3]), inputs,
                float.Parse(f[4]), float.Parse(f[5]), float.Parse(f[6]));
        }
        return cat;
    }

    public DishInfo? ById(string id) => _byId.TryGetValue(id, out var d) ? d : null;

    public IReadOnlyList<(string Group, float Weight)> MixFor(string district) =>
        DistrictMix.TryGetValue(district, out var w)
            ? new[] { ("Workers", w[0]), ("Hipsters", w[1]), ("Elite", w[2]) }
            : new[] { ("Workers", 1 / 3f), ("Hipsters", 1 / 3f), ("Elite", 1 / 3f) };
}