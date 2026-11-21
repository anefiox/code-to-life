using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeToLife.Insights;

/// <summary>Where to send explanation requests. The key only ever comes from an environment variable.</summary>
public sealed record AiConfig(string Label, Uri Endpoint, string? Key, string Model)
{
    public override string ToString() => $"{Label} (model {Model})";

    /// <summary>
    /// <c>--ai openrouter</c>, or the URL of an OpenAI-compatible server such as Unsloth Studio, LM
    /// Studio, Ollama or llama.cpp. A URL may point at the server's web page
    /// (<c>http://host:8888/chat</c>): the API is assumed to live at <c>/v1</c> on the same origin
    /// unless the URL already ends in <c>/v1</c>. Returns null when <paramref name="ai"/> isn't set.
    /// </summary>
    public static AiConfig? From(string? ai, string? model, Func<string, string?> env)
    {
        if (ai is null)
        {
            if (model is not null) throw new ArgumentException("--ai-model needs --ai.");
            return null;
        }
        var choice = ai.Trim();
        if (choice.Equals("openrouter", StringComparison.OrdinalIgnoreCase))
        {
            var key = Blank(env("OPENROUTER_API_KEY")) ?? Blank(env("CODETOLIFE_AI_KEY"))
                ?? throw new ArgumentException("Set OPENROUTER_API_KEY to use OpenRouter.");
            return new AiConfig("OpenRouter", new Uri("https://openrouter.ai/api/v1/chat/completions"), key, Blank(model) ?? "openrouter/auto");
        }
        if (!Uri.TryCreate(choice, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            throw new ArgumentException("--ai must be \"openrouter\" or an http(s) URL such as http://localhost:8888.");
        if (url.UserInfo.Length > 0) throw new ArgumentException("Put the API key in CODETOLIFE_AI_KEY, not in the URL.");
        var path = url.AbsolutePath.TrimEnd('/');
        var origin = url.GetLeftPart(UriPartial.Authority);
        var api = path.EndsWith("/v1", StringComparison.Ordinal) ? origin + path : origin + "/v1";
        return new AiConfig(url.Authority, new Uri(api + "/chat/completions"), Blank(env("CODETOLIFE_AI_KEY")), Blank(model) ?? "default");
    }

    static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Writes explanations with any OpenAI-compatible chat completions API.</summary>
public static partial class AiClient
{
    const int CodeLimit = 12000;
    const int NeighbourLimit = 15;
    static readonly HttpClient Shared = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>The messages that ask for one explanation: the guidance, then the code and what it connects to.</summary>
    public static IReadOnlyList<(string Role, string Content)> Prompt(GraphIndex index, string id)
    {
        var source = Explanations.ReadSource(index, id);
        var isType = index.Types.TryGetValue(id, out var type);
        var owner = isType ? type : index.TypeOf(id);
        string Describe(string other) => $"{index.Display(other)} ({index.Location(other)})";

        IEnumerable<string> usedBy, uses;
        if (isType)
        {
            usedBy = index.EdgesTo(id).Where(e => e.Kind is not ("nested" or "uses")).Select(e => e.Source).Distinct();
            uses = index.EdgesFrom(id).Where(e => e.Kind is not ("nested" or "uses")).Select(e => e.Target).Distinct();
        }
        else
        {
            usedBy = index.CallsTo(id).Select(c => c.Source).Distinct();
            uses = index.CallsFrom(id).Select(c => c.Target).Distinct();
        }
        var code = source.Code.Length > CodeLimit ? source.Code[..CodeLimit] + "\n… (truncated)" : source.Code;
        var lines = new List<string>
        {
            $"Explain this {source.Kind} from a C# project.",
            $"Name: {source.Name}",
            $"File: {source.File} (lines {source.Start}–{source.End})",
        };
        if (owner is not null && !string.IsNullOrEmpty(owner.Namespace)) lines.Add($"Namespace: {owner.Namespace}, project {owner.Project}");
        var usedByList = usedBy.Take(NeighbourLimit).Select(Describe).ToList();
        var usesList = uses.Take(NeighbourLimit).Select(Describe).ToList();
        if (usedByList.Count > 0) lines.Add("Used by: " + string.Join("; ", usedByList));
        if (usesList.Count > 0) lines.Add("Uses: " + string.Join("; ", usesList));
        lines.AddRange(["", "```csharp", code, "```", "", "Reply with only the explanation, as plain text with no heading."]);
        return [("system", Explanations.Guidance), ("user", string.Join('\n', lines))];
    }

    /// <summary>Reasoning models may wrap their thinking in tags; keep only the answer.</summary>
    public static string CleanAnswer(string? text)
    {
        var answer = Thinking().Replace(text ?? "", "");
        var close = ThinkingClose().Match(answer);
        if (close.Success) answer = answer[(close.Index + close.Length)..];
        answer = answer.Trim().Trim('"', '\'', '“', '”').Trim();
        answer = Heading().Replace(answer, "").Trim().Trim('"', '\'', '“', '”').Trim();
        return answer.Length > Explanations.SummaryLimit ? answer[..Explanations.SummaryLimit].Trim() : answer;
    }

    public static async Task<string> AskAsync(AiConfig config, IReadOnlyList<(string Role, string Content)> messages,
        HttpClient? http = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["model"] = config.Model,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["temperature"] = 0.3,
            ["max_tokens"] = 700,
            ["stream"] = false,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, config.Endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (config.Key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Key);
        if (config.Label == "OpenRouter") request.Headers.Add("X-Title", "Code to Life");

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout ?? TimeSpan.FromMinutes(3));
        HttpResponseMessage response;
        string text;
        try
        {
            response = await (http ?? Shared).SendAsync(request, timer.Token);
            text = await response.Content.ReadAsStringAsync(timer.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{config.Label} could not be reached (timed out).");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"{config.Label} could not be reached ({ex.HttpRequestError}: {ex.Message}).");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var hint = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? config.Label == "OpenRouter" ? " Check OPENROUTER_API_KEY." : " Set CODETOLIFE_AI_KEY to an API key for this server."
                    : "";
                throw new InvalidOperationException($"{config.Label} returned {(int)response.StatusCode}.{hint} {text[..Math.Min(300, text.Length)]}".TrimEnd());
            }
        }
        string? content;
        try { content = JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{config.Label} did not return JSON. Is {config.Endpoint} an OpenAI-compatible API?");
        }
        var answer = CleanAnswer(content);
        if (answer.Length == 0) throw new InvalidOperationException($"{config.Label} returned an empty answer.");
        return answer;
    }

    /// <summary>Asks the AI about one type or member and saves the answer.</summary>
    public static async Task<(string Id, ExplanationEntry Entry)> ExplainAsync(AiConfig config, GraphIndex index, string id, HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        var answer = await AskAsync(config, Prompt(index, id), http, cancellationToken: cancellationToken);
        return (id, Explanations.Save(index, id, answer));
    }

    [GeneratedRegex(@"<(think|thinking|reasoning)>[\s\S]*?</\1>", RegexOptions.IgnoreCase)]
    private static partial Regex Thinking();

    [GeneratedRegex(@"</(?:think|thinking|reasoning)>", RegexOptions.IgnoreCase | RegexOptions.RightToLeft)]
    private static partial Regex ThinkingClose();

    [GeneratedRegex(@"^(?:\*\*)?(?:summary|explanation)(?::\*\*|\*\*:|:)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Heading();
}
