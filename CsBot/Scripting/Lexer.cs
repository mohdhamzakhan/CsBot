using System.Globalization;

namespace CsBot.Scripting;

public sealed class ScriptException : Exception
{
    public int Line { get; }
    public ScriptException(int line, string msg) : base($"Line {line}: {msg}") { Line = line; }
}

public sealed record ScriptInput(string Name, string Label, double Default, double? Min, double? Max, double Step);

internal enum TT { Num, Id, Str, Op, NL, End }
internal sealed record Tok(TT T, string S, double N, int Line);

internal static class Lexer
{
    private static readonly string[] Two = { ":=", "==", "!=", "<=", ">=", "&&", "||" };
    private const string One = "+-*/%^()[],?:<>=!;";

    public static List<Tok> Lex(string s)
    {
        var t = new List<Tok>(); int i = 0, line = 1;
        void NL() { if (t.Count > 0 && t[^1].T != TT.NL) t.Add(new Tok(TT.NL, "\n", 0, line)); }
        while (i < s.Length)
        {
            char ch = s[i];
            if (ch == '\n') { NL(); line++; i++; continue; }
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (char.IsDigit(ch) || (ch == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                int st = i; while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                if (i + 1 < s.Length && (s[i] == 'e' || s[i] == 'E') &&
                    (char.IsDigit(s[i + 1]) || ((s[i + 1] == '-' || s[i + 1] == '+') && i + 2 < s.Length && char.IsDigit(s[i + 2]))))
                { i += 2; while (i < s.Length && char.IsDigit(s[i])) i++; }
                string txt = s[st..i];
                if (!double.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) throw new ScriptException(line, $"Bad number '{txt}'");
                t.Add(new Tok(TT.Num, txt, d, line)); continue;
            }
            if (char.IsLetter(ch) || ch == '_')
            {
                int st = i; while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '.')) i++;
                string id = s[st..i].TrimEnd('.');
                if (id.StartsWith("ta.")) id = id[3..]; else if (id.StartsWith("math.")) id = id[5..];
                t.Add(new Tok(TT.Id, id, 0, line)); continue;
            }
            if (ch == '"' || ch == '\'')
            {
                char q = ch; int st = ++i;
                while (i < s.Length && s[i] != q && s[i] != '\n') i++;
                if (i >= s.Length || s[i] != q) throw new ScriptException(line, "Unterminated string");
                t.Add(new Tok(TT.Str, s[st..i], 0, line)); i++; continue;
            }
            string? two = i + 1 < s.Length ? Two.FirstOrDefault(x => x[0] == ch && x[1] == s[i + 1]) : null;
            if (two != null) { t.Add(new Tok(TT.Op, two, 0, line)); i += 2; continue; }
            if (One.IndexOf(ch) >= 0) { t.Add(new Tok(TT.Op, ch.ToString(), 0, line)); i++; continue; }
            throw new ScriptException(line, $"Unexpected character '{ch}'");
        }
        NL(); t.Add(new Tok(TT.End, "", 0, line));
        return t;
    }
}
