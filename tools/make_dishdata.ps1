# make_dishdata.ps1 - regenerate dishdata.csv from ModKit `content dump` JSONs.
# KEYS (id + ingredients) are ASSET names - the names recipes and Items.NameOf use.
# The `name` column is the display name, for UI only.
# Caps and group scores are estimates (not in the dump). Re-run after every game patch.
# Usage (from repo root): powershell -ExecutionPolicy Bypass -File tools\make_dishdata.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dump = Join-Path $root 'dumps'
$out  = Join-Path $root 'NivalisMenuPlanner.Core\Data\dishdata.csv'

$items   = Get-Content (Join-Path $dump 'items.json')   -Raw -Encoding UTF8 | ConvertFrom-Json
$recipes = Get-Content (Join-Path $dump 'recipes.json') -Raw -Encoding UTF8 | ConvertFrom-Json

# asset -> item record; on duplicate assets (Kvass), prefer the buyable one
$itemByAsset = @{}
foreach ($i in $items) {
    if ($itemByAsset.ContainsKey($i.asset)) {
        if ($i.buy -and -not $itemByAsset[$i.asset].buy) { $itemByAsset[$i.asset] = $i }
        Write-Host "WARN duplicate asset: $($i.asset)"
    } else { $itemByAsset[$i.asset] = $i }
}
$recipeByOutput = @{}
foreach ($r in $recipes) { $recipeByOutput[$r.output] = $r }

# unit cost: buy price if purchasable, else the craft cost of the recipe producing it (depth 2)
function UnitCost([string]$asset, [int]$depth) {
    if ($itemByAsset.ContainsKey($asset) -and $itemByAsset[$asset].buy -and $itemByAsset[$asset].basePrice -gt 0) {
        return [double]$itemByAsset[$asset].basePrice
    }
    if ($depth -lt 2 -and $recipeByOutput.ContainsKey($asset)) {
        $c = 0.0
        foreach ($inp in $recipeByOutput[$asset].inputs) { $c += (UnitCost $inp.item ($depth + 1)) * [double]$inp.amount }
        return $c
    }
    return 0.0
}

# cap = real input cost x type factor (ESTIMATE - dish prices are not in the dump)
$factor = @{ Drink = 3.5; Snack = 2.8; Dessert = 1.8; Soup = 1.9; Salad = 1.9
             Sandwich = 2.1; Meat = 2.1; Fish = 2.2; Seafood = 2.2; Pizza = 2.0 }

$warn = New-Object System.Collections.Generic.List[string]
$rows = New-Object System.Collections.Generic.List[string]
$rows.Add('id,name,type,cap,hip,wrk,elt,ingredients')

$venue = @($recipes | Where-Object { -not $_.playerOnly -and -not $_.disabled })
foreach ($r in $venue) {
    if (-not $factor.ContainsKey($r.type)) { $warn.Add("unknown type '$($r.type)' on $($r.output)"); continue }
    $names = @(); $tags = @(); $cost = 0.0
    foreach ($inp in $r.inputs) {
        if (-not $itemByAsset.ContainsKey($inp.item)) { $warn.Add("unknown input '$($inp.item)' in $($r.output)"); continue }
        $it = $itemByAsset[$inp.item]
        if ($it.asset -match ',') { $warn.Add("comma in asset '$($it.asset)' - CSV key would mis-split") }
        if ($it.name -match ',') { $warn.Add("comma in name '$($it.name)' - CSV column would mis-split") }
        $names += $it.asset
        if ($it.tags) { $tags += @($it.tags) }
        $c = UnitCost $inp.item 0
        if ($c -le 0) { $warn.Add("zero-cost input '$($inp.item)' in $($r.output)") }
        $cost += $c * [double]$inp.amount
    }
    $cap = [math]::Round($cost * $factor[$r.type] * 2) / 2          # nearest 0.50 lims

    # group-score ESTIMATES (per-dish satisfaction is not in the dump)
    $hip = 60; $wrk = 60; $elt = 60
    if ($tags -contains 'AlternativeProtein') { $hip += 25; $wrk -= 15 }
    if ($cost -le 6) { $wrk += 20; $hip -= 5 }
    if (($r.type -eq 'Fish' -or $r.type -eq 'Seafood') -and $cost -ge 12) { $elt += 25; $wrk -= 10 }
    if ($r.type -eq 'Dessert' -or ($r.type -eq 'Drink' -and ($tags -contains 'Sweet'))) { $hip += 10 }
    $hip = [math]::Max(5, [math]::Min(100, $hip))
    $wrk = [math]::Max(5, [math]::Min(100, $wrk))
    $elt = [math]::Max(5, [math]::Min(100, $elt))

    $id = $r.output                                            # ASSET name - matches Items.NameOf
    $display = $itemByAsset[$r.output].name
    $rows.Add("$id,$display,$($r.type),$([int]($cap * 100)),$([int]$hip),$([int]$wrk),$([int]$elt),$($names -join ';')")
}

$body = @($rows | Select-Object -Skip 1 | Sort-Object)
[System.IO.Directory]::CreateDirectory((Split-Path -Parent $out)) | Out-Null
[System.IO.File]::WriteAllLines($out, (@($rows[0]) + $body), (New-Object System.Text.UTF8Encoding($false)))

$po = @($recipes | Where-Object { $_.playerOnly })
Write-Host "recipes: $($recipes.Count) total, $($venue.Count) venue, $($po.Count) player-only"
Write-Host "wrote $out ($($body.Count) dishes)"
$warn | ForEach-Object { Write-Host "WARN $_" }
Write-Host "--- preview ---"
@($rows[0]) + @($body | Select-Object -First 3) | ForEach-Object { Write-Host $_ }