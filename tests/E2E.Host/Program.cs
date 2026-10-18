using System.Net;
using System.Text;
using System.Text.Json;
using E2E.Host;

// Startet den E2E-Aufbau mit einem grossen simulierten Tenant und wartet (fuer den Python-/Gateway-Lauf). Ctrl+C beendet.
var big = FakeGraph.LargeIssues(issueCount: 30, postsPerIssue: 4, postChars: 40_000);
big["EXBIG"] = FakeGraph.IssueJson("EXBIG", [FakeGraph.PostText(1_800_000)]); // mehrteiliger Content
var limitOverride = Environment.GetEnvironmentVariable("E2E_MAX_RESPONSE_BYTES"); // Negativkontrolle: Waechter praktisch abschalten
await using var host = await E2eHost.StartAsync(FakeGraph.Handler(big),
    limitOverride is null ? null : o => o.MaxResponseBytes = int.Parse(limitOverride));
Console.WriteLine("MCP_URL=" + host.McpUrl);
Console.WriteLine("READY");
await Task.Delay(Timeout.Infinite);

public static class FakeGraph
{
    public const string PostUnit = "<p class=\"x\">Größe äöü 日本語 😀🎉 \"zitat\" \\pfad\\ & <b>fett</b></p>\n";

    public static string PostText(int chars)
    {
        var sb = new StringBuilder();
        while (sb.Length < chars) sb.Append(PostUnit);
        return sb.ToString(0, chars - (char.IsHighSurrogate(sb[chars - 1]) ? 1 : 0));
    }

    public static string IssueJson(string id, string[] posts, string service = "Exchange Online") => JsonSerializer.Serialize(new
    {
        id, title = $"Titel {id}", service, status = "investigating", classification = "incident", origin = "microsoft", isResolved = false,
        startDateTime = "2026-10-01T00:00:00Z", lastModifiedDateTime = "2026-10-02T00:00:00Z", feature = "F", featureGroup = "G",
        impactDescription = "Auswirkung " + id, details = new[] { new { name = "NotifyInApp", value = "True" } },
        posts = posts.Select(p => new { createdDateTime = "2026-10-01T01:00:00Z", postType = "regular", description = new { contentType = "html", content = p } })
    });

    public static Dictionary<string, string> LargeIssues(int issueCount, int postsPerIssue, int postChars)
    {
        var post = PostText(postChars);
        return Enumerable.Range(0, issueCount).ToDictionary(i => $"EX{i:D4}", i => IssueJson($"EX{i:D4}", Enumerable.Repeat(post, postsPerIssue).ToArray()));
    }

    /// <summary>Simulierter Graph: issues-Liste (eine Seite), issues/{id}; <paramref name="other"/> liefert beliebige weitere Bodies (z. B. messages).</summary>
    public static Func<HttpRequestMessage, HttpResponseMessage> Handler(Dictionary<string, string> issues, Func<HttpRequestMessage, string?>? other = null) => request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        string? body = other?.Invoke(request);
        if (body is null && path.EndsWith("/serviceAnnouncement/issues", StringComparison.OrdinalIgnoreCase))
            body = "{\"value\":[" + string.Join(",", issues.Values) + "]}";
        else if (body is null && path.Contains("/serviceAnnouncement/issues/", StringComparison.OrdinalIgnoreCase))
            body = issues.GetValueOrDefault(path[(path.LastIndexOf('/') + 1)..]);
        return body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":{\"code\":\"NotFound\",\"message\":\"x\"}}", Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    };
}
