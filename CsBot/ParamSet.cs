namespace CsBot.Research;

/// <summary>One concrete set of strategy parameters (name -> value).</summary>
public sealed class ParamSet
{
    private readonly SortedDictionary<string, double> _v;
    public ParamSet(IDictionary<string, double> values) => _v = new SortedDictionary<string, double>(values);

    public double this[string name] => _v[name];
    public double Get(string name, double fallback = 0) => _v.TryGetValue(name, out var x) ? x : fallback;
    public int GetInt(string name, int fallback = 0) => (int)Math.Round(Get(name, fallback));
    public IReadOnlyDictionary<string, double> Values => _v;

    public override string ToString() => string.Join(", ", _v.Select(kv => $"{kv.Key}={kv.Value:G6}"));
}

/// <summary>Inclusive range swept in fixed steps, e.g. fast EMA 5..50 step 5.</summary>
public sealed record Axis(string Name, double Min, double Max, double Step)
{
    public int Count => Step <= 0 || Max < Min ? 0 : (int)Math.Floor((Max - Min) / Step + 1e-9) + 1;
    public double ValueAt(int i) => Math.Round(Min + i * Step, 10);

    public static Axis From(ParamRange r) => new(r.Key, r.From, r.To, r.Step);   // CsBot.ParamRange (Optimizer.cs)

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Parameter name is empty.");
        if (Count < 1) throw new ArgumentException($"Bad range for '{Name}': need Step > 0 and Max >= Min.");
    }
}

/// <summary>A freshly built strategy plus the rebalance band StrategyRegistry chose for it.</summary>
public readonly record struct Built(IStrategy Strategy, double Band);
