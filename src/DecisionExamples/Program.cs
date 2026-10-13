using System.Text.Json;
using DecisionExamples;
using Microsoft.Extensions.Configuration;

// Config (env vars win over user-secrets): FOUNDRY_ENDPOINT, FOUNDRY_MODEL (deployment, default Decision-1), FOUNDRY_API_KEY (optional).
var config = new ConfigurationBuilder().AddUserSecrets<Program>().AddEnvironmentVariables().Build();
var endpoint = config["FOUNDRY_ENDPOINT"] ?? throw new InvalidOperationException("Set FOUNDRY_ENDPOINT.");
var deployment = config["FOUNDRY_MODEL"] ?? "Decision-1";
var poster = new ApiPoster(endpoint, config["FOUNDRY_API_KEY"]);
var client = new DecisionClient(poster, deployment);

var runs = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--runs="))?[7..], out var r) ? r : 3;
var only = args.FirstOrDefault(a => a.StartsWith("--scenario="))?[11..];
var probe = args.Contains("--probe");

if (args.Contains("--aiops-final"))
{
    // --baseline=name[:limit],... runs the same enriched variants on chat deployments; --skip-decision omits Decision-1.
    var bl = (args.FirstOrDefault(a => a.StartsWith("--baseline="))?[11..] ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => s.Split(':')).Select(p => (Name: p[0], Limit: p.Length > 1 && int.TryParse(p[1], out var n) ? n : 0));
    var parts = new List<string>();
    if (!args.Contains("--skip-decision")) parts.Add(await Diagnostic.RunFinalAsync(new DecisionPredictor(client, deployment), deployment));
    foreach (var (b, lim) in bl)
        parts.Add(await Diagnostic.RunFinalAsync(new ChatPredictor(poster, b, AiopsData.Domain), b, lim));
    var md = string.Join("\n\n", parts);
    Console.WriteLine(md);
    var d = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "results");
    Directory.CreateDirectory(d);
    await File.WriteAllTextAsync(Path.Combine(d, $"aiops-final-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md"), md);
    return;
}

if (args.Contains("--aiops-diag"))
{
    var n = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--n="))?[4..], out var dn) ? dn : 200;
    var md = await Diagnostic.RunAsync(client, deployment, n);
    Console.WriteLine(md);
    var d = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "results");
    Directory.CreateDirectory(d);
    await File.WriteAllTextAsync(Path.Combine(d, $"aiops-diag-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md"), md);
    return;
}

if (args.Contains("--aiops"))
{
    // Baselines are comma-separated chat deployments, each optionally capped per category as name:N (e.g. my-reasoning-deployment:60).
    var baselines = (args.FirstOrDefault(a => a.StartsWith("--baseline="))?[11..] ?? config["FOUNDRY_BASELINE"] ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => s.Split(':')).Select(p => (Name: p[0], Limit: p.Length > 1 && int.TryParse(p[1], out var n) ? n : 0)).ToList();
    int Arg(string name) => int.TryParse(args.FirstOrDefault(a => a.StartsWith($"--{name}="))?[(name.Length + 3)..], out var v) ? v : 0;
    var limit = Arg("limit");
    var skipRca = args.Contains("--no-rca");

    Case[] all = [.. AiopsData.LoadHdfsTest(), .. (skipRca ? [] : AiopsData.LoadRca())];
    var main = AiopsData.Subsample(all, limit);
    var results = new Dictionary<string, List<CaseResult>>();
    Console.WriteLine($"Running {main.Length} AIOps cases on {deployment}...");
    results[deployment] = await Benchmark.RunModelAsync(new DecisionPredictor(client, deployment), main, 1, false);
    foreach (var (b, bLimit) in baselines)
    {
        var sub = AiopsData.Subsample(main, bLimit);
        Console.WriteLine($"Running baseline {b} on {sub.Length} cases...");
        results[b] = await Benchmark.RunModelAsync(new ChatPredictor(poster, b, AiopsData.Domain), sub, 1, false);
    }

    var refs = AiopsData.LoadReferences();
    var md = Benchmark.Report(results, main, "AIOps benchmark");
    Console.WriteLine("Calibrating HDFS threshold on held-out blocks...");
    var calCases = AiopsData.LoadHdfsCalibration();
    var calib = new Dictionary<string, List<CaseResult>> { [deployment] = await Benchmark.RunModelAsync(new DecisionPredictor(client, deployment), calCases, 1, false) };
    foreach (var (b, bLimit) in baselines)
        calib[b] = await Benchmark.RunModelAsync(new ChatPredictor(poster, b, AiopsData.Domain), AiopsData.Subsample(calCases, bLimit / 2), 1, false);
    md += Benchmark.CalibratedSection(calib, results, AiopsData.HdfsCategory);
    md += AiopsData.ReferenceSection(refs);
    md += AiopsData.PairedSection(deployment, results, refs);
    Console.WriteLine(md);

    var aiopsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "results");
    Directory.CreateDirectory(aiopsDir);
    var aiopsStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    await File.WriteAllTextAsync(Path.Combine(aiopsDir, $"aiops-{aiopsStamp}.md"), md);
    await File.WriteAllTextAsync(Path.Combine(aiopsDir, $"aiops-{aiopsStamp}.json"),
        JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

if (args.Contains("--benchmark") || args.Contains("--external"))
{
    var external = args.Contains("--external");
    var baseline = args.FirstOrDefault(a => a.StartsWith("--baseline="))?[11..] ?? config["FOUNDRY_BASELINE"];
    var repeats = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--repeats="))?[10..], out var rp) ? rp : external ? 1 : 3;
    var perLabel = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--per-label="))?[12..], out var pl) ? pl : 30;
    var cases = external ? await ExternalData.LoadAsync(perLabel) : Dataset.All;
    var byModel = new Dictionary<string, List<CaseResult>>();
    Console.WriteLine($"Running {cases.Length} cases on {deployment}...");
    byModel[deployment] = await Benchmark.RunModelAsync(new DecisionPredictor(client, deployment), cases, repeats, !external);
    if (!string.IsNullOrWhiteSpace(baseline))
    {
        // Adversarial text is only sent to Decision-1 unless --adversarial-baselines is passed, to avoid tripping other models' abuse monitoring.
        const string injCategory = "ext: prompt-injection (deepset)";
        var sendAdversarial = !external || args.Contains("--adversarial-baselines");
        var baseCases = sendAdversarial ? cases : [.. cases.Where(c => c.Category != injCategory)];
        Console.WriteLine($"Running baseline {baseline} on {baseCases.Length} cases...");
        byModel[baseline] = await Benchmark.RunModelAsync(new ChatPredictor(poster, baseline), baseCases, 1, !external);
    }
    var md = Benchmark.Report(byModel, cases, external ? "External benchmark" : "Benchmark");
    if (external)
    {
        const string injCat = "ext: prompt-injection (deepset)";
        var calCases = await ExternalData.LoadInjectionCalibrationAsync(perLabel * 2);
        Console.WriteLine($"Calibrating thresholds on {calCases.Length} held-out train cases...");
        var calib = new Dictionary<string, List<CaseResult>>
        {
            [deployment] = await Benchmark.RunModelAsync(new DecisionPredictor(client, deployment), calCases, 1, false),
        };
        if (!string.IsNullOrWhiteSpace(baseline) && args.Contains("--adversarial-baselines"))
            calib[baseline] = await Benchmark.RunModelAsync(new ChatPredictor(poster, baseline), calCases, 1, false);
        md += Benchmark.CalibratedSection(calib, byModel, injCat);
    }
    Console.WriteLine(md);
    var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "results");
    Directory.CreateDirectory(dir);
    var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    var prefix = external ? "external" : "benchmark";
    await File.WriteAllTextAsync(Path.Combine(dir, $"{prefix}-{stamp}.md"), md);
    await File.WriteAllTextAsync(Path.Combine(dir, $"{prefix}-{stamp}.json"),
        JsonSerializer.Serialize(byModel, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

if (probe)
{
    var s = Scenarios.All.First(x => only is null || x.Id == only);
    var res = await client.CallAsync(s);
    Console.WriteLine(res.Error ?? "OK");
    Console.WriteLine(res.Raw);
    return;
}

var outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "results");
Directory.CreateDirectory(outDir);
var report = new List<ScenarioResult>();

foreach (var s in Scenarios.All.Where(s => only is null || s.Id == only))
{
    var res = await Evaluator.RunAsync(client, s, runs);
    report.Add(res);
    var order = res.OrderFlips is null ? "" : $" order-flips={res.OrderFlips}";
    Console.WriteLine($"{(res.PassRate >= 1 ? "PASS" : "FAIL")} {s.Id,-20} pass={res.PassRate:P0} p50={res.P50LatencyMs:F0}ms spread={res.MaxSpread:F3}{order} | {res.Summary}");
}

var path = Path.Combine(outDir, $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { deployment, runs, report }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Saved {Path.GetFullPath(path)}");

public partial class Program;
