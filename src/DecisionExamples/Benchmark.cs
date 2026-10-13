using System.Text;

namespace DecisionExamples;

public record CaseResult(Case Case, double? PTrue, string? Choice, bool Correct, double Confidence, double VariantAccuracy,
    bool Flipped, double Spread, List<Prediction> Base, List<Prediction> Variants)
{
    public bool Errored => Base.All(p => p.Error is not null);
}

public static class Benchmark
{
    public static async Task<List<CaseResult>> RunModelAsync(IPredictor m, Case[] cases, int repeats, bool withVariants = true)
    {
        var results = new List<CaseResult>();
        var done = 0;
        foreach (var c in cases)
        {
            if (++done % 25 == 0) Console.WriteLine($"  {m.Name}: {done}/{cases.Length}");
            var baseline = new List<Prediction>();
            for (var i = 0; i < repeats; i++) baseline.Add(await m.PredictAsync(c, c.Instructions, false));

            var variants = new List<Prediction>();
            if (withVariants)
            {
                foreach (var p in c.Paraphrases) variants.Add(await m.PredictAsync(c, p, false));
                if (c.QType == "choice") variants.Add(await m.PredictAsync(c, c.Instructions, true));
            }

            var ok = baseline.Where(p => p.Error is null).ToList();
            double? pTrue = c.QType == "noul" && ok.Count > 0 ? ok.Average(p => p.PTrue ?? 0.5) : null;
            var choice = c.QType == "choice" ? ok.GroupBy(p => p.Choice).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key : null;
            object? main = ok.Count == 0 ? null : pTrue is not null ? pTrue >= 0.5 : choice;

            var spread = c.QType == "noul" && ok.Count > 1 ? ok.Max(p => p.PTrue ?? 0) - ok.Min(p => p.PTrue ?? 0)
                : ok.Count > 1 ? 1 - ok.Count(p => p.Choice == choice) / (double)ok.Count : 0;
            var okVar = variants.Where(p => p.Error is null).ToList();

            results.Add(new CaseResult(c, pTrue, choice, main is not null && IsCorrect(c, main),
                ok.Count == 0 ? 0 : ok.Average(p => p.Confidence),
                okVar.Count == 0 ? 0 : okVar.Count(p => IsCorrect(c, Label(c, p))) / (double)okVar.Count,
                main is not null && okVar.Any(p => !Equals(Label(c, p), main)), spread, baseline, variants));
        }
        return results;
    }

    static object Label(Case c, Prediction p) => c.QType == "noul" ? (p.PTrue ?? 0.5) >= 0.5 : p.Choice ?? "";

    static bool IsCorrect(Case c, object label) =>
        c.QType == "noul" ? label is bool b && b == (c.Expected[0] == "true") : label is string s && c.Expected.Contains(s);

    static double Pct(IEnumerable<bool> xs) { var l = xs.ToList(); return l.Count == 0 ? double.NaN : l.Count(x => x) / (double)l.Count; }
    static double Percentile(List<double> xs, double p) { xs.Sort(); return xs.Count == 0 ? double.NaN : xs[(int)Math.Min(xs.Count - 1, Math.Floor(p * xs.Count))]; }

    static double Auc(List<(double score, bool pos)> data)
    {
        var pos = data.Where(d => d.pos).ToList(); var neg = data.Where(d => !d.pos).ToList();
        if (pos.Count == 0 || neg.Count == 0) return double.NaN;
        return pos.Sum(p => neg.Sum(n => p.score > n.score ? 1.0 : p.score == n.score ? 0.5 : 0)) / (pos.Count * neg.Count);
    }

    static double Ece(List<(double score, bool pos)> data, int bins = 5)
    {
        if (data.Count == 0) return double.NaN;
        return Enumerable.Range(0, bins).Sum(b =>
        {
            var inBin = data.Where(d => Math.Min(bins - 1, (int)(d.score * bins)) == b).ToList();
            return inBin.Count == 0 ? 0 : Math.Abs(inBin.Average(d => d.pos ? 1.0 : 0) - inBin.Average(d => d.score)) * inBin.Count / data.Count;
        });
    }

    static List<(double score, bool pos)> Data(List<CaseResult> rs) =>
        rs.Where(r => r.Case.QType == "noul" && r.PTrue is not null).Select(r => (r.PTrue!.Value, r.Case.Expected[0] == "true")).ToList();

    // Pick the threshold on calibration data, then score the test split with it. Returns a markdown section.
    public static string CalibratedSection(Dictionary<string, List<CaseResult>> calib, Dictionary<string, List<CaseResult>> test, string category)
    {
        var sb = new StringBuilder($"\n## Threshold calibrated on a held-out split ({category})\n\n| model | threshold (from train) | accuracy at 0.5 | accuracy at calibrated threshold | F1 at calibrated threshold |\n|---|---|---|---|---|\n");
        foreach (var (name, calRs) in calib)
        {
            var cal = calRs.Where(r => !r.Errored && r.PTrue is not null).ToList();
            var grid = Enumerable.Range(1, 19).Select(i => i * 0.05).ToList();
            double Acc(List<CaseResult> rs, double t) => rs.Count(r => (r.PTrue >= t) == (r.Case.Expected[0] == "true")) / (double)rs.Count;
            var best = grid.Max(t => Acc(cal, t));
            var bestTs = grid.Where(t => Acc(cal, t) >= best - 1e-9).ToList();
            var thr = bestTs[bestTs.Count / 2];
            var te = test[name].Where(r => r.Case.Category == category && !r.Errored && r.PTrue is not null).ToList();
            double tp = te.Count(r => r.PTrue >= thr && r.Case.Expected[0] == "true"), fp = te.Count(r => r.PTrue >= thr && r.Case.Expected[0] != "true"), fn = te.Count(r => r.PTrue < thr && r.Case.Expected[0] == "true");
            sb.AppendLine($"| {name} | {thr:F2} (train acc {best:P0}, n={cal.Count}) | {Acc(te, 0.5):P0} | {Acc(te, thr):P0} (n={te.Count}) | {2 * tp / (2 * tp + fp + fn):F2} |");
        }
        return sb.ToString();
    }

    static double MacroF1(List<CaseResult> rs)
    {
        string Expected(CaseResult r) => r.Case.Expected[0];
        string Predicted(CaseResult r) => r.Errored ? "(error)" : r.Case.QType == "noul" ? ((r.PTrue ?? 0.5) >= 0.5 ? "true" : "false") : r.Choice ?? "(none)";
        var labels = rs.Select(Expected).Distinct().ToList();
        if (labels.Count == 0) return double.NaN;
        return labels.Average(l =>
        {
            double tp = rs.Count(r => Expected(r) == l && Predicted(r) == l);
            double fp = rs.Count(r => Expected(r) != l && Predicted(r) == l);
            double fn = rs.Count(r => Expected(r) == l && Predicted(r) != l);
            return tp == 0 ? 0 : 2 * tp / (2 * tp + fp + fn);
        });
    }

    public static string Report(Dictionary<string, List<CaseResult>> byModel, Case[] cases, string title = "Benchmark")
    {
        var sb = new StringBuilder();
        var names = byModel.Keys.ToList();
        var clear = byModel.ToDictionary(k => k.Key, k => k.Value.Where(r => !r.Case.Ambiguous && !r.Errored).ToList());
        // Accuracy keeps failed/blocked calls in the denominator as misses; probability metrics need a real answer.
        var clearAll = byModel.ToDictionary(k => k.Key, k => k.Value.Where(r => !r.Case.Ambiguous).ToList());
        string RowAll(string label, Func<List<CaseResult>, string> f) => $"| {label} | {string.Join(" | ", names.Select(n => f(clearAll[n])))} |";
        string Row(string label, Func<List<CaseResult>, string> f) => $"| {label} | {string.Join(" | ", names.Select(n => f(clear[n])))} |";
        string Header(string first) => $"| {first} | {string.Join(" | ", names)} |\n|---|{string.Join("|", names.Select(_ => "---"))}|";

        sb.AppendLine($"# {title} ({cases.Count(c => !c.Ambiguous)} clear cases, {cases.Count(c => c.Ambiguous)} ambiguous)\n");

        sb.AppendLine("## Accuracy and macro-F1 on clear cases (original wording)\n").AppendLine(Header("Category"));
        foreach (var cat in cases.Select(c => c.Category).Distinct())
            sb.AppendLine(RowAll(cat, rs => { var x = rs.Where(r => r.Case.Category == cat).ToList(); return x.Count == 0 ? "not run" : $"{x.Count(r => r.Correct)}/{x.Count} ({x.Count(r => r.Correct) / (double)x.Count:P0}), F1 {MacroF1(x):F2}"; }));
        sb.AppendLine(RowAll("**overall**", rs => $"**{rs.Count(r => r.Correct)}/{rs.Count}**"));

        if (clear.Values.Any(rs => rs.Any(r => r.Variants.Count > 0)))
        {
            sb.AppendLine("\n## Robustness to rewording / option order\n").AppendLine(Header("Metric"));
            sb.AppendLine(Row("accuracy across variants", rs => $"{rs.Average(r => r.VariantAccuracy):P0}"));
            sb.AppendLine(Row("cases whose answer flips under a variant", rs => $"{rs.Count(r => r.Flipped)}/{rs.Count}"));
            sb.AppendLine(Row("max answer spread across identical repeats", rs => $"{rs.Max(r => r.Spread):F3}"));
        }

        sb.AppendLine("\n## Probability quality (yes/no questions)\n").AppendLine(Header("Metric"));
        sb.AppendLine(Row("AUC", rs => $"{Auc(Data(rs)):F3}"));
        sb.AppendLine(Row("Brier (lower is better)", rs => Data(rs).Count == 0 ? "n/a" : $"{Data(rs).Average(d => Math.Pow(d.score - (d.pos ? 1 : 0), 2)):F3}"));
        sb.AppendLine(Row("ECE, 5 bins (lower is better)", rs => $"{Ece(Data(rs)):F3}"));

        if (cases.Any(c => c.Ambiguous))
        {
            sb.AppendLine("\n## Does uncertainty track difficulty? (mean confidence)\n").AppendLine(Header("Group"));
            sb.AppendLine(Row("clear cases", rs => $"{rs.Average(r => r.Confidence):F2}"));
            sb.AppendLine($"| ambiguous cases | {string.Join(" | ", names.Select(n => { var a = byModel[n].Where(r => r.Case.Ambiguous && !r.Errored).ToList(); return a.Count == 0 ? "n/a" : $"{a.Average(r => r.Confidence):F2}"; }))} |");
        }

        sb.AppendLine("\n## Latency and tokens per call\n").AppendLine(Header("Metric"));
        List<Prediction> Preds(string n) => byModel[n].SelectMany(r => r.Base.Concat(r.Variants)).Where(p => p.Error is null).ToList();
        sb.AppendLine($"| p50 latency (ms) | {string.Join(" | ", names.Select(n => $"{Percentile(Preds(n).Select(p => p.LatencyMs).ToList(), 0.5):F0}"))} |");
        sb.AppendLine($"| p95 latency (ms) | {string.Join(" | ", names.Select(n => $"{Percentile(Preds(n).Select(p => p.LatencyMs).ToList(), 0.95):F0}"))} |");
        sb.AppendLine($"| input tokens / call | {string.Join(" | ", names.Select(n => $"{Preds(n).Average(p => p.InTokens):F0}"))} |");
        sb.AppendLine($"| output tokens / call | {string.Join(" | ", names.Select(n => $"{Preds(n).Average(p => p.OutTokens):F1}"))} |");

        var ts = new[] { 0.1, 0.3, 0.5, 0.7, 0.9 };
        sb.AppendLine($"\n## Accuracy vs. decision threshold (yes/no, clear cases)\n\n| model | {string.Join(" | ", ts)} |\n|---|{string.Join("|", ts.Select(_ => "---"))}|");
        foreach (var n in names)
            sb.AppendLine($"| {n} | {string.Join(" | ", ts.Select(t => $"{Pct(Data(clear[n]).Select(d => (d.score >= t) == d.pos)):P0}"))} |");

        sb.AppendLine("\n## Misses (clear cases)\n");
        foreach (var n in names)
        {
            sb.AppendLine($"**{n}**");
            var misses = clear[n].Where(r => !r.Correct || r.Flipped).ToList();
            if (misses.Count == 0) sb.AppendLine("- none");
            if (misses.Count > 20) sb.AppendLine($"- {misses.Count} misses, showing the first 20");
            foreach (var r in misses.Take(20))
                sb.AppendLine($"- `{r.Case.Id}` ({r.Case.Nuance}): {(r.Case.QType == "noul" ? $"P(true)={r.PTrue:F2}, expected {r.Case.Expected[0]}" : $"chose {r.Choice}, expected {string.Join("/", r.Case.Expected)}")}{(r.Correct ? "" : " WRONG")}{(r.Flipped ? " [flips under variant]" : "")}");
            sb.AppendLine();
        }

        foreach (var (n, rs) in byModel)
        {
            var errs = rs.SelectMany(r => r.Base.Concat(r.Variants)).Where(p => p.Error is not null).ToList();
            if (errs.Count > 0) sb.AppendLine($"> {n}: {errs.Count} failed calls, e.g. {errs[0].Error}\n");
        }
        return sb.ToString();
    }
}
