using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using E2E.Host;
using Xunit.Abstractions;

namespace McpServer.Tests;

/// <summary>
/// Q-API-2 E2E: echter McpServer (MCP-SDK-Serialisierung, Streamable HTTP/SSE) + echte ApiServer-Controller/Services, Graph simuliert.
/// Gemessen wird die Groesse jedes SSE-Events auf dem Draht. Client-Limit (httpx2/mcp-Python, vom FastMCP-Gateway genutzt): 1 MiB je Event.
/// </summary>
public class McpE2eTests(ITestOutputHelper output)
{
    private const int ClientEventLimit = 1_048_576;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private sealed record McpResult(string Text, bool IsError, int MaxEventBytes, int Events);

    private static async Task<McpResult> CallAsync(string mcpUrl, string tool, object arguments)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = tool, arguments } });
        using var request = new HttpRequestMessage(HttpMethod.Post, mcpUrl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await Http.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var raw = Encoding.UTF8.GetString(bytes);
        Assert.True(response.IsSuccessStatusCode, raw.Length > 300 ? raw[..300] : raw);

        // SSE-Events sind durch eine Leerzeile getrennt; Groesse = komplette Event-Bytes (konservativ: inkl. "event:"/"data:"-Praefix).
        var events = raw.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var max = events.Max(e => Encoding.UTF8.GetByteCount(e));
        var dataLine = events.SelectMany(e => e.Split('\n')).First(l => l.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        using var doc = JsonDocument.Parse(dataLine);
        var result = doc.RootElement.GetProperty("result");
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        return new McpResult(text, result.TryGetProperty("isError", out var e2) && e2.GetBoolean(), max, events.Length);
    }

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    [Fact]
    public async Task Legacy_issues_tool_with_a_multi_megabyte_tenant_returns_a_marked_partial_result_below_the_client_limit()
    {
        var issues = FakeGraph.LargeIssues(issueCount: 30, postsPerIssue: 4, postChars: 40_000); // ~4,8 Mio. Zeichen, 12+ MB escaped
        await using var host = await E2eHost.StartAsync(FakeGraph.Handler(issues));

        var result = await CallAsync(host.McpUrl, "get_service_health_issues", new { });

        output.WriteLine($"get_service_health_issues: text={result.Text.Length} chars, maxSseEvent={result.MaxEventBytes} bytes");
        Assert.False(result.IsError);
        Assert.True(result.MaxEventBytes < ClientEventLimit, $"SSE-Event {result.MaxEventBytes} >= 1 MiB");
        using var doc = JsonDocument.Parse(result.Text);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("isPartial").GetBoolean());
        Assert.Equal(30, doc.RootElement.GetProperty("totalItems").GetInt32());
        Assert.Contains("get_service_health_issue_content", doc.RootElement.GetProperty("hint").GetString());
    }

    [Theory]
    [InlineData(300_000, false)]
    [InlineData(690_000, false)]
    [InlineData(700_000, false)]
    [InlineData(702_000, false)]
    [InlineData(1_048_000, false)]
    [InlineData(1_500_000, false)]
    [InlineData(40_000, true)]   // Unicode (ae/Emoji): JSON-Escaping vervielfacht die Groesse
    [InlineData(120_000, true)]
    [InlineData(400_000, true)]
    public async Task Messages_response_sizes_around_the_limit_never_exceed_the_client_event_limit(int chars, bool unicode)
    {
        var unit = unicode ? "äöü😀日本" : "abcdefghij";
        var items = new List<string>();
        var perItem = 2_000;
        for (var total = 0; total < chars; total += perItem)
        {
            var text = string.Concat(Enumerable.Repeat(unit, perItem / unit.Length + 1))[..(perItem / 2 * 2)];
            if (char.IsHighSurrogate(text[^1])) text = text[..^1];
            items.Add(JsonSerializer.Serialize(new { id = $"MC{items.Count}", title = "t", body = new { contentType = "html", content = text } }));
        }

        var graphBody = "{\"value\":[" + string.Join(",", items) + "]}";
        await using var host = await E2eHost.StartAsync(FakeGraph.Handler([], r => r.RequestUri!.AbsolutePath.EndsWith("/serviceAnnouncement/messages") ? graphBody : null));

        var result = await CallAsync(host.McpUrl, "get_message_center_messages", new { });

        output.WriteLine($"messages chars={chars} unicode={unicode}: graphBody={Encoding.UTF8.GetByteCount(graphBody)} B, maxSseEvent={result.MaxEventBytes} B, truncated={result.Text.Contains("\"truncated\":true")}");
        Assert.False(result.IsError);
        Assert.True(result.MaxEventBytes < ClientEventLimit, $"SSE-Event {result.MaxEventBytes} >= 1 MiB");
        if (!result.Text.Contains("\"truncated\":true", StringComparison.Ordinal))
        {
            Assert.Equal(graphBody, result.Text); // passt es, kommt es unveraendert an
        }
        else
        {
            using var doc = JsonDocument.Parse(result.Text);
            Assert.True(doc.RootElement.GetProperty("isPartial").GetBoolean());
            Assert.Equal(items.Count, doc.RootElement.GetProperty("totalItems").GetInt32());
        }
    }

    [Fact]
    public async Task A_single_oversized_item_is_reported_without_failing_the_call()
    {
        var huge = JsonSerializer.Serialize(new { id = "MC-HUGE", body = new string('x', 3_000_000) });
        await using var host = await E2eHost.StartAsync(FakeGraph.Handler([], r => r.RequestUri!.AbsolutePath.EndsWith("/serviceAnnouncement/messages") ? "{\"value\":[" + huge + "]}" : null));

        var result = await CallAsync(host.McpUrl, "get_message_center_messages", new { });

        Assert.False(result.IsError);
        Assert.True(result.MaxEventBytes < ClientEventLimit);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal(0, doc.RootElement.GetProperty("returnedItems").GetInt32());
        Assert.Equal("MC-HUGE", doc.RootElement.GetProperty("omittedIds")[0].GetString());
    }

    [Fact]
    public async Task Content_of_a_huge_post_is_retrievable_losslessly_in_order_through_mcp()
    {
        var original = FakeGraph.PostText(1_800_000); // Unicode, Emoji, HTML, Escapes
        var issues = new Dictionary<string, string> { ["EX9999"] = FakeGraph.IssueJson("EX9999", [original]) };
        await using var host = await E2eHost.StartAsync(FakeGraph.Handler(issues));

        var sb = new StringBuilder();
        string? token = null;
        string? version = null;
        var chunks = 0;
        var maxEvent = 0;
        do
        {
            var r = await CallAsync(host.McpUrl, "get_service_health_issue_content", token is null
                ? new { contentId = "issue:EX9999:post:0" } as object
                : new { contentId = "issue:EX9999:post:0", continuationToken = token });
            maxEvent = Math.Max(maxEvent, r.MaxEventBytes);
            Assert.True(r.MaxEventBytes < ClientEventLimit, $"Chunk {chunks}: SSE-Event {r.MaxEventBytes} >= 1 MiB");
            using var doc = JsonDocument.Parse(r.Text);
            var root = doc.RootElement;
            Assert.Equal("ok", root.GetProperty("status").GetString());
            Assert.Equal(sb.Length, root.GetProperty("offset").GetInt32()); // lueckenlos, richtige Reihenfolge
            sb.Append(root.GetProperty("text").GetString());
            var v = root.GetProperty("contentVersion").GetString();
            Assert.Equal(version ??= v, v);
            token = root.GetProperty("hasMore").GetBoolean() ? root.GetProperty("continuationToken").GetString() : null;
            chunks++;
        } while (token is not null && chunks < 100);

        output.WriteLine($"content: {chunks} chunks, max SSE event {maxEvent} bytes, total {sb.Length} chars");
        Assert.True(chunks > 3);
        Assert.Equal(original, sb.ToString());
        Assert.Equal(Sha(original), Sha(sb.ToString()));
        Assert.Equal(Encoding.UTF8.GetBytes(original), Encoding.UTF8.GetBytes(sb.ToString())); // byte-identisch
    }

    [Fact]
    public async Task Continuation_token_survives_an_api_restart_with_the_same_key_and_changed_content_is_detected()
    {
        var original = FakeGraph.PostText(1_500_000);
        var issues = new Dictionary<string, string> { ["EX1"] = FakeGraph.IssueJson("EX1", [original]) };

        string token;
        string firstText;
        await using (var host1 = await E2eHost.StartAsync(FakeGraph.Handler(issues)))
        {
            var r = await CallAsync(host1.McpUrl, "get_service_health_issue_content", new { contentId = "issue:EX1:post:0" });
            using var d = JsonDocument.Parse(r.Text);
            token = d.RootElement.GetProperty("continuationToken").GetString()!;
            firstText = d.RootElement.GetProperty("text").GetString()!;
        }

        // "Neustart": neuer Prozess-Aufbau, gleicher Schluessel, gleicher Inhalt
        await using (var host2 = await E2eHost.StartAsync(FakeGraph.Handler(issues)))
        {
            var r = await CallAsync(host2.McpUrl, "get_service_health_issue_content", new { contentId = "issue:EX1:post:0", continuationToken = token });
            using var d = JsonDocument.Parse(r.Text);
            Assert.Equal("ok", d.RootElement.GetProperty("status").GetString());
            Assert.Equal(firstText.Length, d.RootElement.GetProperty("offset").GetInt32());
        }

        // Inhalt hat sich geaendert -> content_changed, keine Daten
        var changed = new Dictionary<string, string> { ["EX1"] = FakeGraph.IssueJson("EX1", [original + "NEU"]) };
        await using (var host3 = await E2eHost.StartAsync(FakeGraph.Handler(changed)))
        {
            var r = await CallAsync(host3.McpUrl, "get_service_health_issue_content", new { contentId = "issue:EX1:post:0", continuationToken = token });
            Assert.Contains("content_changed", r.Text);
            Assert.DoesNotContain("\"text\"", r.Text);
        }

        // Anderer Schluessel (Rotation) -> Token ungueltig, keine Daten
        await using (var host4 = await E2eHost.StartAsync(FakeGraph.Handler(issues), o => o.ContinuationSigningKey = E2eHost.TestKey("rotated")))
        {
            var r = await CallAsync(host4.McpUrl, "get_service_health_issue_content", new { contentId = "issue:EX1:post:0", continuationToken = token });
            Assert.Contains("invalid_continuation_token", r.Text);
            Assert.DoesNotContain("\"text\"", r.Text);
        }
    }

    [Fact]
    public async Task Legacy_truncation_does_not_make_content_unreachable_and_search_lists_content_refs()
    {
        var issues = FakeGraph.LargeIssues(issueCount: 30, postsPerIssue: 2, postChars: 60_000);
        await using var host = await E2eHost.StartAsync(FakeGraph.Handler(issues));

        var legacy = await CallAsync(host.McpUrl, "get_service_health_issues", new { });
        using var legacyDoc = JsonDocument.Parse(legacy.Text);
        Assert.True(legacyDoc.RootElement.GetProperty("truncated").GetBoolean());
        var anyId = legacyDoc.RootElement.GetProperty("omittedIds").EnumerateArray().Select(x => x.GetString()).First()!; // ein tatsaechlich ausgelassener Eintrag

        var search = await CallAsync(host.McpUrl, "search_service_health_issues", new { service = "Exchange Online", maxResults = 100 });
        output.WriteLine($"search: {search.Text.Length} chars, maxSseEvent={search.MaxEventBytes}");
        using var searchDoc = JsonDocument.Parse(search.Text);
        Assert.Equal(30, searchDoc.RootElement.GetProperty("itemCount").GetInt32());
        Assert.False(searchDoc.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.True(search.MaxEventBytes < ClientEventLimit);

        var issue = await CallAsync(host.McpUrl, "get_service_health_issue", new { id = anyId, projection = "details" });
        Assert.Contains($"issue:{anyId}:post:1", issue.Text);

        var content = await CallAsync(host.McpUrl, "get_service_health_issue_content", new { contentId = $"issue:{anyId}:post:1" });
        using var contentDoc = JsonDocument.Parse(content.Text);
        Assert.Equal("ok", contentDoc.RootElement.GetProperty("status").GetString());
    }
}
