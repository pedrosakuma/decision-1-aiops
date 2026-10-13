using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Azure.Core;
using Azure.Identity;

namespace DecisionExamples;

public record PostResult(string Raw, double LatencyMs, string? Error);

// Shared HTTP plumbing: auth (api-key or Entra ID) and retry on 429/5xx honoring Retry-After.
public class ApiPoster
{
    const int MaxRetries = 6;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(180) };
    readonly TokenCredential _cred = new DefaultAzureCredential();
    readonly string? _key;
    public string BaseUrl { get; }

    public ApiPoster(string endpoint, string? key)
    {
        var e = endpoint.TrimEnd('/');
        foreach (var marker in new[] { "/providers/", "/models", "/openai", "/api/projects" })
        {
            var i = e.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i > 0) e = e[..i];
        }
        BaseUrl = e;
        _key = key;
    }

    public async Task<PostResult> PostAsync(string path, string body)
    {
        for (var attempt = 0; ; attempt++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (_key is not null) req.Headers.Add("api-key", _key);
            else
            {
                var token = await _cred.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), default);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            }
            var sw = Stopwatch.StartNew();
            try
            {
                var resp = await _http.SendAsync(req);
                var raw = await resp.Content.ReadAsStringAsync();
                sw.Stop();
                if ((int)resp.StatusCode is 429 or >= 500 && attempt < MaxRetries)
                {
                    await Task.Delay(resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))));
                    continue;
                }
                return new PostResult(raw, sw.Elapsed.TotalMilliseconds, resp.IsSuccessStatusCode ? null : $"HTTP {(int)resp.StatusCode}: {Truncate(raw)}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (attempt < MaxRetries) { await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt))); continue; }
                return new PostResult("", sw.Elapsed.TotalMilliseconds, ex.Message);
            }
        }
    }

    static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
