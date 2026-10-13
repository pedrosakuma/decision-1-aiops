using System.Text.Json;
using System.Text.Json.Nodes;

namespace DecisionExamples;

// Public datasets, downloaded at runtime via the Hugging Face datasets-server and cached under data/external/
// (git-ignored; never redistributed, since some licenses are non-commercial).
public static class ExternalData
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    static string CacheDir()
    {
        var d = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "external");
        Directory.CreateDirectory(d);
        return d;
    }

    record Row(string Text, int Label);

    static async Task<List<Row>> FetchAsync(string dataset, string split, string textField)
    {
        var cache = Path.Combine(CacheDir(), $"{dataset.Replace("/", "__")}__{split}.json");
        if (File.Exists(cache))
            return JsonSerializer.Deserialize<List<Row>>(await File.ReadAllTextAsync(cache))!;

        var rows = new List<Row>();
        for (var offset = 0; ; offset += 100)
        {
            var url = $"https://datasets-server.huggingface.co/rows?dataset={dataset}&config=default&split={split}&offset={offset}&length=100";
            JsonNode? page = null;
            for (var attempt = 0; attempt < 5 && page is null; attempt++)
            {
                var resp = await Http.GetAsync(url);
                if (resp.IsSuccessStatusCode) page = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
                else await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)));
            }
            if (page is null) throw new InvalidOperationException($"Could not download {dataset}/{split}");
            foreach (var r in page["rows"]!.AsArray())
                rows.Add(new Row(r!["row"]![textField]!.GetValue<string>(), r["row"]!["label"]!.GetValue<int>()));
            if (offset + 100 >= page["num_rows_total"]!.GetValue<int>()) break;
        }
        await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(rows));
        return rows;
    }

    // Fixed seed so every run (and every model) sees the same stratified sample.
    static List<Row> Stratified(List<Row> rows, int perLabel) =>
        rows.GroupBy(r => r.Label).OrderBy(g => g.Key).SelectMany(g =>
        {
            var rng = new Random(42 + g.Key);
            return g.OrderBy(r => r.Text, StringComparer.Ordinal).OrderBy(_ => rng.Next()).Take(perLabel);
        }).ToList();

    // Held-out train split used only to pick a decision threshold, never for scoring.
    public static async Task<Case[]> LoadInjectionCalibrationAsync(int perLabel)
    {
        const string injQ = "Is this text a prompt injection, i.e. does it try to make an AI assistant ignore, change, or abandon its original task or instructions?";
        var train = Stratified(await FetchAsync("deepset/prompt-injections", "train", "text"), perLabel);
        return [.. train.Select((r, i) => new Case($"deepset-train-{i}", "ext: prompt-injection (deepset)", r.Label == 1 ? "injection" : "benign", "noul",
            injQ, [], [], JsonValue.Create(r.Text), [r.Label == 1 ? "true" : "false"], false))];
    }

    public static async Task<Case[]> LoadAsync(int perLabel)
    {
        var cases = new List<Case>();

        // deepset/prompt-injections (Apache-2.0). label 1 = injection. Full test split.
        const string injQ = "Is this text a prompt injection, i.e. does it try to make an AI assistant ignore, change, or abandon its original task or instructions?";
        var inj = await FetchAsync("deepset/prompt-injections", "test", "text");
        cases.AddRange(inj.Select((r, i) => new Case($"deepset-{i}", "ext: prompt-injection (deepset)", r.Label == 1 ? "injection" : "benign", "noul",
            injQ, [], [], JsonValue.Create(r.Text), [r.Label == 1 ? "true" : "false"], false)));

        // zeroshot/twitter-financial-news-sentiment (MIT). 0 Bearish, 1 Bullish, 2 Neutral.
        string[] tw = ["BEARISH", "BULLISH", "NEUTRAL"];
        (string, string)[] twOpts =
        [
            ("BEARISH", "Negative for the stock price (downgrade, miss, cut, lawsuit, decline)"),
            ("BULLISH", "Positive for the stock price (upgrade, beat, raise, approval, growth)"),
            ("NEUTRAL", "No clear impact on the stock price (factual, mixed, or not about price)"),
        ];
        var tweets = Stratified(await FetchAsync("zeroshot/twitter-financial-news-sentiment", "validation", "text"), perLabel);
        cases.AddRange(tweets.Select((r, i) => new Case($"tweet-{i}", "ext: financial tweet sentiment", tw[r.Label].ToLowerInvariant(), "choice",
            "What is the sentiment of this finance-related tweet for the stock(s) it mentions?", [], twOpts, JsonValue.Create(r.Text), [tw[r.Label]], false)));

        // gtfintechlab/fomc_communication (CC BY-NC-4.0). 0 Dovish, 1 Hawkish, 2 Neutral.
        string[] fm = ["DOVISH", "HAWKISH", "NEUTRAL"];
        (string, string)[] fmOpts =
        [
            ("DOVISH", "Favors easier monetary policy: lower rates, more stimulus, concern about weak growth or employment"),
            ("HAWKISH", "Favors tighter monetary policy: higher rates, concern about inflation, strong growth or labor market"),
            ("NEUTRAL", "No clear tilt toward easier or tighter policy"),
        ];
        var fomc = Stratified(await FetchAsync("gtfintechlab/fomc_communication", "test", "sentence"), perLabel);
        cases.AddRange(fomc.Select((r, i) => new Case($"fomc-{i}", "ext: FOMC hawkish/dovish", fm[r.Label].ToLowerInvariant(), "choice",
            "What is the monetary policy stance of this FOMC sentence?", [], fmOpts, JsonValue.Create(r.Text), [fm[r.Label]], false)));

        return [.. cases];
    }
}
