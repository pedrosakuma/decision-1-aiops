using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DecisionExamples;

// AIOps cases built by scripts/aiops/prepare.py from Loghub HDFS_v1 and RCAEval RE2-OB.
// The raw data stays under data/external/aiops (git-ignored, never redistributed).
public static class AiopsData
{
    public const string HdfsCategory = "ext: AIOps HDFS block anomaly (Loghub)";
    public const string RcaServiceCategory = "ext: AIOps root-cause service (RCAEval RE2-OB)";
    public const string RcaFaultCategory = "ext: AIOps fault type (RCAEval RE2-OB)";
    public const string Domain = "an SRE / AIOps team";

    static string Dir() => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "external", "aiops");

    const string HdfsQ = "Does this block's log activity indicate a failure or abnormal behavior, as opposed to a normal block lifecycle?";
    const string ServiceQ = "Which service is the root cause of the incident?";
    const string FaultQ = "What kind of fault was injected into the root-cause service?";

    static readonly (string, string)[] ServiceOptions =
    [
        ("adservice", "The ad service is the origin of the fault"),
        ("cartservice", "The shopping cart service is the origin of the fault"),
        ("checkoutservice", "The checkout service is the origin of the fault"),
        ("currencyservice", "The currency conversion service is the origin of the fault"),
        ("emailservice", "The email service is the origin of the fault"),
        ("frontend", "The frontend is the origin of the fault"),
        ("paymentservice", "The payment service is the origin of the fault"),
        ("productcatalogservice", "The product catalog service is the origin of the fault"),
        ("recommendationservice", "The recommendation service is the origin of the fault"),
        ("redis", "The redis cache is the origin of the fault"),
        ("shippingservice", "The shipping service is the origin of the fault"),
    ];

    static readonly (string, string)[] FaultOptions =
    [
        ("cpu", "CPU stress on the service's container"),
        ("mem", "Memory stress on the service's container"),
        ("disk", "Disk I/O stress on the service's container"),
        ("delay", "Network delay injected on the service"),
        ("loss", "Network packet loss injected on the service"),
        ("socket", "Socket exhaustion on the service"),
    ];

    static IEnumerable<JsonNode> ReadLines(string file) =>
        File.ReadLines(Path.Combine(Dir(), file)).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!);

    static Case Hdfs(JsonNode n, string prefix) =>
        new(prefix + n["id"]!.GetValue<string>(), HdfsCategory, n["label"]!.GetValue<int>() == 1 ? "anomaly" : "normal", "noul", HdfsQ, [], [],
            n["state"]!.DeepClone(), [n["label"]!.GetValue<int>() == 1 ? "true" : "false"], false);

    // Evenly spaced subset per category, so a cheaper or slower model sees a representative, reproducible sample.
    public static Case[] Subsample(Case[] cases, int limitPerCategory) =>
        limitPerCategory <= 0 ? cases : [.. cases.GroupBy(c => c.Category).SelectMany(g =>
        {
            var l = g.ToList();
            return l.Count <= limitPerCategory ? l : Enumerable.Range(0, limitPerCategory).Select(i => l[(int)((long)i * l.Count / limitPerCategory)]);
        })];

    public static Case[] LoadHdfsTest() => [.. ReadLines("hdfs_test.jsonl").Select(n => Hdfs(n, ""))];

    public static Case[] LoadHdfsCalibration() => [.. ReadLines("hdfs_calib.jsonl").Select(n => Hdfs(n, "calib-"))];

    public static Case[] LoadRca() => [.. ReadLines("rca_cases.jsonl").SelectMany(n => new[]
    {
        new Case(n["id"]!.GetValue<string>() + "#service", RcaServiceCategory, n["service"]!.GetValue<string>(), "choice", ServiceQ, [], ServiceOptions,
            n["state"]!.DeepClone(), [n["service"]!.GetValue<string>()], false),
        new Case(n["id"]!.GetValue<string>() + "#fault", RcaFaultCategory, n["fault"]!.GetValue<string>(), "choice", FaultQ, [], FaultOptions,
            n["state"]!.DeepClone(), [n["fault"]!.GetValue<string>()], false),
    })];

    // Non-LLM reference points computed by prepare.py: logistic regression on event counts (HDFS) and a simple
    // "largest metric deviation" rule (RCA). Each is a per-case correctness map for the paired comparison.
    public record Reference(string Name, string Category, Dictionary<string, bool> Correct, double? Auc);

    public static List<Reference> LoadReferences()
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir(), "classical_baselines.json")))!;
        var refs = new List<Reference>();

        var h = root["hdfs"]!;
        var ids = h["ids"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        var labels = h["labels"]!.AsArray().Select(x => x!.GetValue<int>()).ToList();
        var rule = h["rule_any_exception_event"]!["pred"]!.AsArray().Select(x => x!.GetValue<int>()).ToList();
        refs.Add(new Reference("rule: any exception event", HdfsCategory, ids.Zip(rule.Zip(labels), (id, p) => (id, ok: p.First == p.Second)).ToDictionary(t => t.id, t => t.ok), null));
        foreach (var (name, node) in h.AsObject().Where(kv => kv.Key.StartsWith("logreg_")))
        {
            var prob = node!["prob"]!.AsArray().Select(x => x!.GetValue<double>()).ToList();
            refs.Add(new Reference($"logreg ({name[13..]} labeled blocks)", HdfsCategory,
                ids.Select((id, i) => (id, ok: (prob[i] >= 0.5) == (labels[i] == 1))).ToDictionary(t => t.id, t => t.ok), node["auc"]!.GetValue<double>()));
        }

        var r = root["rca_heuristic"]!;
        var cases = ReadLines("rca_cases.jsonl").ToList();
        var sp = r["service_pred"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        var fp = r["fault_pred"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        refs.Add(new Reference("rule: largest metric deviation", RcaServiceCategory,
            cases.Select((c, i) => (id: c["id"]!.GetValue<string>() + "#service", ok: sp[i] == c["service"]!.GetValue<string>())).ToDictionary(t => t.id, t => t.ok), null));
        refs.Add(new Reference("rule: kind of largest metric deviation", RcaFaultCategory,
            cases.Select((c, i) => (id: c["id"]!.GetValue<string>() + "#fault", ok: fp[i] == c["fault"]!.GetValue<string>())).ToDictionary(t => t.id, t => t.ok), null));
        return refs;
    }

    // Wilson score interval, McNemar exact test (two-sided) on cases answered by both models.
    public static string PairedSection(string reference, Dictionary<string, List<CaseResult>> byModel, List<Reference> refs)
    {
        var sb = new StringBuilder("\n## Paired comparison against " + reference + " (accuracy, 95% Wilson CI, exact McNemar p)\n\n");
        sb.AppendLine("A failed call (e.g. blocked by a content filter) counts as incorrect. `only ref` / `only other` count cases where just one of the two is right.\n");
        sb.AppendLine("| category | other | n | accuracy ref | accuracy other | only ref | only other | p |\n|---|---|---|---|---|---|---|---|");
        var refRs = byModel[reference];
        foreach (var cat in refRs.Select(r => r.Case.Category).Distinct())
        {
            var refMap = refRs.Where(r => r.Case.Category == cat).ToDictionary(r => r.Case.Id, r => !r.Errored && r.Correct);
            var others = byModel.Where(kv => kv.Key != reference)
                .Select(kv => (kv.Key, Map: kv.Value.Where(r => r.Case.Category == cat).ToDictionary(r => r.Case.Id, r => !r.Errored && r.Correct)))
                .Concat(refs.Where(x => x.Category == cat).Select(x => (x.Name, Map: x.Correct)));
            foreach (var (name, map) in others)
            {
                var common = refMap.Keys.Where(map.ContainsKey).ToList();
                if (common.Count == 0) continue;
                int a = common.Count(id => refMap[id] && !map[id]), b = common.Count(id => !refMap[id] && map[id]);
                int okRef = common.Count(id => refMap[id]), okOther = common.Count(id => map[id]);
                sb.AppendLine($"| {cat} | {name} | {common.Count} | {Fmt(okRef, common.Count)} | {Fmt(okOther, common.Count)} | {a} | {b} | {FmtP(McNemar(a, b))} |");
            }
        }
        return sb.ToString();
    }

    public static string Fmt(int k, int n)
    {
        const double z = 1.96;
        double p = (double)k / n, d = 1 + z * z / n;
        var centre = (p + z * z / (2 * n)) / d;
        var half = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / d;
        return $"{p:P1} [{Math.Max(0, centre - half):P0}-{Math.Min(1, centre + half):P0}]";
    }

    public static string FmtP(double p) => p < 0.0001 ? "<0.0001" : p.ToString("0.####");

    public static double McNemar(int a, int b)
    {
        var n = a + b;
        if (n == 0) return 1;
        var k = Math.Min(a, b);
        double cdf = 0, logHalfN = n * Math.Log(0.5);
        for (var i = 0; i <= k; i++) cdf += Math.Exp(LogChoose(n, i) + logHalfN);
        return Math.Min(1, 2 * cdf);
    }

    static double LogChoose(int n, int k)
    {
        double s = 0;
        for (var i = 1; i <= k; i++) s += Math.Log(n - k + i) - Math.Log(i);
        return s;
    }

    // Accuracy and AUC of each LLM and each reference model, on the same HDFS test blocks.
    public static string ReferenceSection(List<Reference> refs)
    {
        var sb = new StringBuilder("\n## Non-LLM reference points\n\n| reference | category | accuracy | AUC |\n|---|---|---|---|\n");
        foreach (var r in refs)
            sb.AppendLine($"| {r.Name} | {r.Category} | {Fmt(r.Correct.Values.Count(v => v), r.Correct.Count)} | {(r.Auc is null ? "n/a" : r.Auc.Value.ToString("F3"))} |");
        return sb.ToString();
    }
}
