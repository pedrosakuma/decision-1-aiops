using System.Text.Json.Nodes;

namespace DecisionExamples;

public record CallResult(Dictionary<string, Answer> Answers, string Raw, double LatencyMs, string? Error);

public class DecisionClient
{
    readonly ApiPoster _poster;
    readonly string _deployment;

    public DecisionClient(ApiPoster poster, string deployment)
    {
        _poster = poster;
        _deployment = deployment;
    }

    public static JsonObject BuildRequest(string deployment, Scenario s, bool reverseOptions = false)
    {
        var questions = new JsonObject();
        foreach (var q in s.Questions)
        {
            var qo = new JsonObject { ["type"] = q.Type, ["instructions"] = q.Instructions };
            if (q.Criteria.Length > 0)
            {
                var items = reverseOptions ? q.Criteria.Reverse().ToArray() : q.Criteria;
                if (q.Type == "score") // ordered scale: list of levels, lowest to highest
                {
                    var arr = new JsonArray();
                    foreach (var c in q.Criteria) arr.Add(c.Text);
                    qo["criteria"] = arr;
                }
                else
                {
                    var crit = new JsonObject();
                    foreach (var c in items) crit[c.Key] = c.Text;
                    qo["criteria"] = crit;
                }
            }
            questions[q.Name] = qo;
        }
        return new JsonObject { ["model"] = deployment, ["state"] = s.State.DeepClone(), ["questions"] = questions };
    }

    public Task<CallResult> CallAsync(Scenario s, bool reverseOptions = false) =>
        SendAsync(BuildRequest(_deployment, s, reverseOptions));

    public async Task<CallResult> SendAsync(JsonObject request)
    {
        var r = await _poster.PostAsync("/providers/microsoft/v1/systemone", request.ToJsonString());
        return r.Error is not null
            ? new CallResult([], r.Raw, r.LatencyMs, r.Error)
            : new CallResult(ParseAnswers(r.Raw), r.Raw, r.LatencyMs, null);
    }

    static Dictionary<string, Answer> ParseAnswers(string raw)
    {
        var result = new Dictionary<string, Answer>();
        if (JsonNode.Parse(raw)?["answers"] is not JsonObject answers) return result;
        foreach (var (name, node) in answers)
        {
            if (node is null) continue;
            double? prob = null, score = null;
            string? choice = null, type = null;
            var probs = new Dictionary<string, double>();
            if (node is JsonObject o)
            {
                type = o["type"]?.GetValue<string>();
                choice = o["choice"]?.GetValue<string>() ?? o["option"]?.GetValue<string>();
                prob = Num(o["noul"]) ?? Num(o["probability"]) ?? Num(o["p"]);
                score = Num(o["score"]) ?? Num(o["value"]);
                if ((o["probabilities"] ?? o["options"]) is JsonObject po)
                    foreach (var (k, v) in po) if (Num(v) is double d) probs[k] = d;
                if (type == "noul" && prob is null) prob = score;
            }
            else if (Num(node) is double n)
            {
                prob = n; score = n;
            }
            result[name] = new Answer(type ?? "", prob, choice, probs, score);
        }
        return result;
    }

    static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
