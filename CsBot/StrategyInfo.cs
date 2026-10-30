using System.Text.Json;
using CsBot.Scripting;

namespace CsBot;

public sealed record ParamRow(string Key, string Label, double Value, double Default);
public sealed record StrategyDesc(string Id, string Name, string Description, List<ParamRow> Parameters, string? Code);

/// <summary>Describes a strategy in human terms: its name, every parameter with its current value, and (for scripts) the code.</summary>
public static class StrategyInfo
{
    public static StrategyDesc Describe(string id, Dictionary<string, JsonElement>? p, string? code, double? band = null)
    {
        var def = StrategyRegistry.All.FirstOrDefault(s => s.Id == id);
        var rows = new List<ParamRow>(); string name = def?.Name ?? id, description = def?.Description ?? "";
        if (id == "script")
        {
            var tpl = Templates.All.FirstOrDefault(t => t.Code == code);
            name = tpl != null ? tpl.Name + " (script)" : "Custom script";
            description = tpl?.Description ?? "A script you wrote or edited.";
            try
            {
                foreach (var i in ScriptStrategy.Check(code ?? "").Inputs)
                {
                    double v = StrategyRegistry.Num(p, i.Name, i.Default);
                    if (i.Min != null) v = Math.Max(v, i.Min.Value); if (i.Max != null) v = Math.Min(v, i.Max.Value);
                    rows.Add(new ParamRow(i.Name, i.Label, v, i.Default));
                }
            }
            catch (ScriptException) { /* shown without parameters */ }
        }
        else if (def != null)
            foreach (var d in def.Params) rows.Add(new ParamRow(d.Key, d.Label, StrategyRegistry.Num(p, d.Key, d.Default), d.Default));
        if (id == "script") rows.Add(new ParamRow("band", "Rebalance band", band ?? StrategyRegistry.Num(p, "band", 0.15), 0.15));
        return new StrategyDesc(id, name, description, rows, id == "script" ? code : null);
    }

    public static object Costs(CostConfig c) => new { feePct = c.TakerFeePct, gstPct = c.GstPct, slippagePct = c.SlippagePct, tdsPct = c.TdsPct, fundingPer8hPct = c.FundingPer8hPct, maxLeverage = c.MaxLeverage };
}
