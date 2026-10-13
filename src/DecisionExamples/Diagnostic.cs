using System.Text;
using System.Text.Json.Nodes;

namespace DecisionExamples;

// Isolates whether Decision-1's weak HDFS result comes from the question wording, the state, or the output type.
// Uses blocks from the calibration split only, never the test split.
public static class Diagnostic
{
    const string Cat = "diag: HDFS";
    const string AnomalyQ = "Does this block's log activity indicate a failure or abnormal behavior, as opposed to a normal block lifecycle?";
    const string LifecycleQ = "Did this block complete the normal HDFS write lifecycle on all expected replicas (allocated, written, acknowledged and registered)?";
    const string ClassifyQ = "Classify this block's log activity.";

    static readonly (string, string)[] ClassifyOptions =
    [
        ("normal", "The block completed its normal lifecycle on all replicas"),
        ("anomalous", "The block shows a failure, an incomplete lifecycle, or abnormal behavior"),
        ("uncertain", "The evidence is not enough to decide"),
    ];

    static int Count(JsonObject counts, string needle) =>
        counts.Where(kv => kv.Key.Contains(needle, StringComparison.OrdinalIgnoreCase)).Sum(kv => kv.Value!.GetValue<int>());

    // Structure derived from HDFS domain knowledge (default replication 3, standard write pipeline), never from labels.
    static JsonNode Derived(JsonNode raw)
    {
        var counts = raw["event_counts"]!.AsObject();
        int alloc = Count(counts, "allocateBlock"), starts = Count(counts, "Receiving block"),
            done = Count(counts, "Received block"), term = Count(counts, "terminating"),
            reg = Count(counts, "blockMap updated"),
            errs = counts.Where(kv => new[] { "exception", "error", "failed", "timed out", "interrupted" }.Any(w => kv.Key.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .Sum(kv => kv.Value!.GetValue<int>());
        var missing = new JsonArray();
        if (alloc < 1) missing.Add("block allocation");
        if (starts < 3) missing.Add("write started on every replica");
        if (done < 3) missing.Add("write completed on every replica");
        if (term < 3) missing.Add("pipeline acknowledgements terminated on every replica");
        if (reg < 3) missing.Add("replicas registered with the namenode");
        var o = raw.DeepClone().AsObject();
        o["lifecycle_summary"] = new JsonObject
        {
            ["expected_replicas"] = 3,
            ["block_allocations"] = alloc,
            ["replica_writes_started"] = starts,
            ["replica_writes_completed"] = done,
            ["pipeline_acks_terminated"] = term,
            ["replicas_registered_with_namenode"] = reg,
            ["missing_lifecycle_steps"] = missing,
            ["exception_or_error_events"] = errs,
        };
        return o;
    }

    static double Auc(List<(double s, bool pos)> d)
    {
        var pos = d.Where(x => x.pos).ToList(); var neg = d.Where(x => !x.pos).ToList();
        return pos.Count == 0 || neg.Count == 0 ? double.NaN : pos.Sum(p => neg.Sum(n => p.s > n.s ? 1.0 : p.s == n.s ? 0.5 : 0)) / (pos.Count * neg.Count);
    }

    static Case Reframe(Case c, string q, Func<JsonNode, JsonNode> state, bool anomalyPos, string qtype)
    {
        var anomaly = c.Expected[0] == "true";
        return c with
        {
            Category = Cat,
            QType = qtype,
            Instructions = q,
            State = state(c.State),
            Options = qtype == "choice" ? ClassifyOptions : [],
            Expected = qtype == "choice" ? [anomaly ? "anomalous" : "normal"] : [(anomalyPos ? anomaly : !anomaly) ? "true" : "false"],
        };
    }

    // Final comparison on the full HDFS test split. Variant E's threshold is picked on the calibration split only.
    public static async Task<string> RunFinalAsync(IPredictor predictor, string deployment, int limit = 0)
    {
        var calib = AiopsData.Subsample(AiopsData.LoadHdfsCalibration(), limit * 3 / 2);
        var test = AiopsData.Subsample(AiopsData.LoadHdfsTest(), limit);

        Console.WriteLine($"E: calibrating on {calib.Length} blocks...");
        var calE = await Benchmark.RunModelAsync(predictor, [.. calib.Select(c => Reframe(c, LifecycleQ, Derived, false, "noul"))], 1, false);
        // Anomaly score = 1 - P(complete lifecycle).
        double Score(CaseResult r) => 1 - r.PTrue!.Value;
        bool IsAnomaly(CaseResult r) => r.Case.Expected[0] == "false";
        var cal = calE.Where(r => !r.Errored).ToList();
        var grid = Enumerable.Range(1, 19).Select(i => i * 0.05).ToList();
        double Acc(IEnumerable<CaseResult> rs, double t) => rs.Average(r => (Score(r) >= t) == IsAnomaly(r) ? 1.0 : 0);
        var best = grid.Max(t => Acc(cal, t));
        var bestTs = grid.Where(t => Acc(cal, t) >= best - 1e-9).ToList();
        var thr = bestTs[bestTs.Count / 2];

        Console.WriteLine("E: test...");
        var testE = await Benchmark.RunModelAsync(predictor, [.. test.Select(c => Reframe(c, LifecycleQ, Derived, false, "noul"))], 1, false);
        Console.WriteLine("D: test...");
        var testD = await Benchmark.RunModelAsync(predictor, [.. test.Select(c => Reframe(c, ClassifyQ, Derived, true, "choice"))], 1, false);

        var eMap = testE.ToDictionary(r => r.Case.Id, r => !r.Errored && (Score(r) >= thr) == IsAnomaly(r));
        var e05 = testE.ToDictionary(r => r.Case.Id, r => !r.Errored && (Score(r) >= 0.5) == IsAnomaly(r));
        var dMap = testD.ToDictionary(r => r.Case.Id, r => !r.Errored && r.Correct);
        var auc = Auc([.. testE.Where(r => !r.Errored).Select(r => (Score(r), IsAnomaly(r)))]);
        var abst = testD.Count(r => r.Choice == "uncertain");

        var sb = new StringBuilder($"# HDFS final comparison on {test.Length} test blocks ({deployment})\n\n");
        sb.AppendLine($"Variant E threshold on anomaly score: {thr:F2} (calibration accuracy {best:P1}, n={cal.Count}). Variant E AUC: {auc:F3}. Variant D abstentions: {abst}/{testD.Count}.\n");
        sb.AppendLine("| model | accuracy [95% CI] |\n|---|---|");
        sb.AppendLine($"| {deployment} E (lifecycle question + summary), threshold 0.5 | {AiopsData.Fmt(e05.Values.Count(v => v), e05.Count)} |");
        sb.AppendLine($"| {deployment} E, calibrated threshold {thr:F2} | {AiopsData.Fmt(eMap.Values.Count(v => v), eMap.Count)} |");
        sb.AppendLine($"| {deployment} D (choice with abstain, summary) | {AiopsData.Fmt(dMap.Values.Count(v => v), dMap.Count)} |");

        sb.AppendLine($"\n## Paired against non-LLM references (same blocks; exact McNemar)\n\n| {deployment} variant | reference | accuracy ref [CI] | only {deployment} right | only reference right | p |\n|---|---|---|---|---|---|");
        foreach (var rf in AiopsData.LoadReferences().Where(r => r.Category == AiopsData.HdfsCategory))
            foreach (var (name, map) in new[] { ("E calibrated", eMap), ("D", dMap) })
            {
                var common = map.Keys.Where(rf.Correct.ContainsKey).ToList();
                int a = common.Count(id => map[id] && !rf.Correct[id]), b = common.Count(id => !map[id] && rf.Correct[id]);
                sb.AppendLine($"| {name} | {rf.Name} | {AiopsData.Fmt(common.Count(id => rf.Correct[id]), common.Count)} | {a} | {b} | {AiopsData.FmtP(AiopsData.McNemar(a, b))} |");
            }
        return sb.ToString();
    }

    public static async Task<string> RunAsync(DecisionClient client, string deployment, int n)
    {
        var raw = AiopsData.LoadHdfsCalibration().Take(n).ToArray();
        // (name, question, state builder, anomaly-is-positive, qtype)
        var variants = new (string Name, string Q, Func<JsonNode, JsonNode> State, bool AnomalyPos, string QType)[]
        {
            ("A. baseline: 'abnormal?', raw counts", AnomalyQ, s => s.DeepClone(), true, "noul"),
            ("B. lifecycle question, raw counts", LifecycleQ, s => s.DeepClone(), false, "noul"),
            ("C. 'abnormal?', counts + lifecycle summary", AnomalyQ, Derived, true, "noul"),
            ("D. choice normal/anomalous/uncertain, summary", ClassifyQ, Derived, true, "choice"),
            ("E. lifecycle question, counts + summary", LifecycleQ, Derived, false, "noul"),
        };

        var sb = new StringBuilder($"# HDFS diagnostic ({raw.Count(c => c.Expected[0] == "true")} anomalous + {raw.Count(c => c.Expected[0] == "false")} normal calibration blocks, {deployment})\n\n");
        sb.AppendLine("| variant | accuracy @0.5 | AUC | abstained | accuracy when answered | mean P on normal / anomalous |\n|---|---|---|---|---|---|");
        foreach (var v in variants)
        {
            Console.WriteLine($"Running {v.Name}...");
            var cases = raw.Select(c =>
            {
                var anomaly = c.Expected[0] == "true";
                var positive = v.AnomalyPos ? anomaly : !anomaly;
                return c with
                {
                    Category = Cat,
                    QType = v.QType,
                    Instructions = v.Q,
                    State = v.State(c.State),
                    Options = v.QType == "choice" ? ClassifyOptions : [],
                    Expected = v.QType == "choice" ? [anomaly ? "anomalous" : "normal"] : [positive ? "true" : "false"],
                };
            }).ToArray();
            var rs = await Benchmark.RunModelAsync(new DecisionPredictor(client, deployment), cases, 1, false);
            var ok = rs.Where(r => !r.Errored).ToList();
            if (v.QType == "choice")
            {
                var abst = ok.Count(r => r.Choice == "uncertain");
                var answered = ok.Where(r => r.Choice != "uncertain").ToList();
                sb.AppendLine($"| {v.Name} | {ok.Count(r => r.Correct) / (double)ok.Count:P1} | n/a | {abst}/{ok.Count} | {answered.Count(r => r.Correct) / (double)Math.Max(1, answered.Count):P1} | n/a |");
            }
            else
            {
                var data = ok.Select(r => (r.PTrue!.Value, r.Case.Expected[0] == "true")).ToList();
                double MeanP(bool anomalous) => ok.Where(r => (r.Case.Expected[0] == "true") == (anomalous == v.AnomalyPos)).Average(r => r.PTrue!.Value);
                var label = v.AnomalyPos ? "P(abnormal)" : "P(complete lifecycle)";
                sb.AppendLine($"| {v.Name} | {ok.Count(r => r.Correct) / (double)ok.Count:P1} | {Auc(data):F3} | n/a | n/a | {label}: {MeanP(false):F2} / {MeanP(true):F2} |");
            }
        }
        return sb.ToString();
    }
}
