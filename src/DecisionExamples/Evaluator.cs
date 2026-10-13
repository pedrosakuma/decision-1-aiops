namespace DecisionExamples;

public record Trial(Dictionary<string, Answer> Answers, bool Pass, double LatencyMs, string? Error, string Raw);

public record ScenarioResult(string Id, string Title, string Highlight, double PassRate, double P50LatencyMs,
    double MaxSpread, int? OrderFlips, string Summary, List<Trial> Trials, List<Trial> ReversedTrials);

public static class Evaluator
{
    static bool Passes(Scenario s, Dictionary<string, Answer> answers) =>
        s.Checks.All(c => answers.TryGetValue(c.Question, out var a) && c.Passes(a));

    // Primary signal of each question, used for spread/summary: P(true), score, or top-choice probability.
    static double Signal(Answer a) => a.Probability ?? a.Score ?? (a.Probabilities.Count > 0 ? a.Probabilities.Values.Max() : 0);

    public static async Task<ScenarioResult> RunAsync(DecisionClient client, Scenario s, int runs)
    {
        var trials = new List<Trial>();
        var reversed = new List<Trial>();
        for (var i = 0; i < runs; i++) trials.Add(await RunTrial(client, s, false));

        var hasChoice = s.Questions.Any(q => q.Type == "choice");
        if (hasChoice) for (var i = 0; i < runs; i++) reversed.Add(await RunTrial(client, s, true));

        var ok = trials.Where(t => t.Error is null).ToList();
        var lat = trials.Select(t => t.LatencyMs).OrderBy(x => x).ToList();

        var spread = 0.0;
        foreach (var q in s.Questions)
        {
            var vals = ok.Where(t => t.Answers.ContainsKey(q.Name)).Select(t => Signal(t.Answers[q.Name])).ToList();
            if (vals.Count > 1) spread = Math.Max(spread, vals.Max() - vals.Min());
        }

        int? flips = null;
        if (hasChoice)
        {
            flips = 0;
            foreach (var q in s.Questions.Where(q => q.Type == "choice"))
            {
                var a = ok.Select(t => t.Answers.GetValueOrDefault(q.Name)?.Choice).FirstOrDefault();
                flips += reversed.Count(t => t.Answers.GetValueOrDefault(q.Name)?.Choice != a);
            }
        }

        var first = ok.FirstOrDefault();
        var summary = first is null ? trials.FirstOrDefault()?.Error ?? "no data"
            : string.Join(", ", s.Questions.Select(q => first.Answers.TryGetValue(q.Name, out var a)
                ? $"{q.Name}={(a.Choice ?? Signal(a).ToString("F3"))}" : $"{q.Name}=?"));

        return new ScenarioResult(s.Id, s.Title, s.Highlight, trials.Count(t => t.Pass) / (double)trials.Count,
            lat[lat.Count / 2], spread, flips, summary, trials, reversed);
    }

    static async Task<Trial> RunTrial(DecisionClient client, Scenario s, bool reverse)
    {
        var res = await client.CallAsync(s, reverse);
        return new Trial(res.Answers, res.Error is null && Passes(s, res.Answers), res.LatencyMs, res.Error, res.Raw);
    }
}
