using System.Net;
using System.Text;
using System.Text.Json;
using ApiServer.Health;
using ApiServer.Services;
using McpServer.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ApiServer.Tests.Health;

/// <summary>Q-API-2: Groessenwaechter, Fortsetzungs-Tokens, Issue-Suche und verlustfreier Content-Abruf (simuliertes Graph).</summary>
public class ServiceHealthIssueServiceTests
{
    private static string TestKey(string tag) => tag + "-test-only-" + new string('k', 40);

    private static ContinuationTokenService Tokens(Action<ResponseLimitsOptions>? configure = null, TimeProvider? time = null)
    {
        var o = new ResponseLimitsOptions { ContinuationSigningKey = TestKey("unit") };
        configure?.Invoke(o);
        return new ContinuationTokenService(Options.Create(o), time);
    }

    private static (ServiceHealthIssueService Service, T.Harness H) Create(
        Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond, Action<ResponseLimitsOptions>? limits = null, Action<MicrosoftHealthOptions>? health = null)
    {
        var h = T.Create(respond, health);
        var lim = new ResponseLimitsOptions { ContinuationSigningKey = TestKey("unit") };
        limits?.Invoke(lim);
        var service = new ServiceHealthIssueService(h.Runner, new FakeGraphToken(), T.Opt(h.Options), Options.Create(lim),
            new ContinuationTokenService(Options.Create(lim)), new FixedTime(T.Now));
        return (service, h);
    }

    private static string Issue(string id, string service = "Exchange Online", bool resolved = false, string classification = "incident",
        string status = "investigating", string start = "2026-10-01T00:00:00Z", string title = "Titel", string impact = "Impact", string[]? posts = null) =>
        JsonSerializer.Serialize(new
        {
            id, title = $"{title} {id}", service, status, classification, origin = "microsoft", isResolved = resolved,
            startDateTime = start, lastModifiedDateTime = "2026-10-02T00:00:00Z", feature = "F", featureGroup = "G", impactDescription = impact,
            details = new[] { new { name = "NotifyInApp", value = "True" } },
            posts = (posts ?? []).Select(p => new { createdDateTime = "2026-10-01T01:00:00Z", postType = "regular", description = new { contentType = "html", content = p } })
        });

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    private static IssueQuery Q(int max = 25, string? service = null, bool? resolved = null, string? text = null, string? cls = null, string? status = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null) => new(service, resolved, cls, status, from, to, text, max);

    // ---------- ResponseSizeGuard ----------

    [Fact]
    public void Guard_returns_small_responses_unchanged()
    {
        const string json = "{\"value\":[{\"id\":\"A\"}]}";
        Assert.Equal(json, ResponseSizeGuard.Enforce(json, 10_000, "s", "hint"));
    }

    [Fact]
    public void Guard_marks_oversize_collections_and_keeps_a_fitting_prefix()
    {
        var items = string.Join(",", Enumerable.Range(0, 200).Select(i => $"{{\"id\":\"I{i}\",\"body\":\"{new string('x', 1000)}\"}}"));
        var json = $"{{\"value\":[{items}]}}";

        var result = ResponseSizeGuard.Enforce(json, 20_000, "src", "nutze Suche");

        Assert.True(ResponseSizeGuard.EscapedSize(result) <= 20_000);
        using var doc = Parse(result);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.True(root.GetProperty("isPartial").GetBoolean());
        Assert.Equal(200, root.GetProperty("totalItems").GetInt32());
        var returned = root.GetProperty("returnedItems").GetInt32();
        Assert.InRange(returned, 1, 199);
        Assert.Equal(returned, root.GetProperty("value").GetArrayLength());
        Assert.Contains("nutze Suche", root.GetProperty("hint").GetString());
        Assert.Equal("I0", root.GetProperty("availableIds")[0].GetString());
    }

    [Fact]
    public void Guard_counts_unicode_escaping_so_utf8_heavy_content_stays_under_the_limit()
    {
        var items = string.Join(",", Enumerable.Range(0, 100).Select(i => $"{{\"id\":\"I{i}\",\"t\":\"{new string('ä', 800)}\"}}"));
        var json = $"{{\"value\":[{items}]}}";
        Assert.True(Encoding.UTF8.GetByteCount(json) < 300_000); // roh klein, escaped deutlich groesser

        var result = ResponseSizeGuard.Enforce(json, 150_000, "s", "h");

        Assert.True(ResponseSizeGuard.EscapedSize(result) <= 150_000);
        using var doc = Parse(result);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Guard_handles_a_single_oversized_item_and_non_collections()
    {
        var single = $"{{\"value\":[{{\"id\":\"BIG\",\"x\":\"{new string('a', 50_000)}\"}}]}}";
        using (var doc = Parse(ResponseSizeGuard.Enforce(single, 5_000, "s", "h")))
        {
            Assert.Equal(0, doc.RootElement.GetProperty("returnedItems").GetInt32());
            Assert.Equal("BIG", doc.RootElement.GetProperty("availableIds")[0].GetString());
        }

        using var notCollection = Parse(ResponseSizeGuard.Enforce($"{{\"x\":\"{new string('a', 50_000)}\"}}", 5_000, "s", "h"));
        Assert.True(notCollection.RootElement.GetProperty("truncated").GetBoolean());

        using var csvArray = Parse(ResponseSizeGuard.Enforce("[" + string.Join(",", Enumerable.Range(0, 500).Select(i => $"{{\"id\":\"{i}\",\"v\":\"{new string('q', 100)}\"}}")) + "]", 10_000, "s", "h"));
        Assert.True(csvArray.RootElement.GetProperty("returnedItems").GetInt32() > 0);
    }

    // ---------- Fortsetzungs-Tokens ----------

    [Fact]
    public void Token_roundtrip_and_tamper_expiry_and_binding_checks()
    {
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var tokens = Tokens(time: new FixedTime(now));
        var token = tokens.Create("search", "bind1", "https://graph.microsoft.com/v1.0/admin/serviceAnnouncement/issues", 7, "v1");

        Assert.True(tokens.TryValidate(token, "search", "bind1", out var payload, out _));
        Assert.Equal(7, payload.Offset);

        Assert.False(tokens.TryValidate(token + "x", "search", "bind1", out _, out _));
        Assert.False(tokens.TryValidate(token[..^3] + "AAA", "search", "bind1", out _, out _));
        Assert.False(tokens.TryValidate(token, "content", "bind1", out _, out var bindError));
        Assert.Contains("passt nicht", bindError);
        Assert.False(tokens.TryValidate(token, "search", "other", out _, out _));
        Assert.False(tokens.TryValidate("garbage", "search", "bind1", out _, out _));
        Assert.False(tokens.TryValidate(null, "search", "bind1", out _, out _));

        var expired = Tokens(time: new FixedTime(now.AddHours(1)));
        Assert.False(expired.TryValidate(token, "search", "bind1", out _, out var expiredError));
        Assert.Contains("abgelaufen", expiredError);
    }

    [Fact]
    public void Token_signed_with_a_different_key_is_rejected()
    {
        var a = Tokens();
        var b = Tokens(o => o.ContinuationSigningKey = TestKey("another"));
        Assert.False(b.TryValidate(a.Create("search", "b", null, 0, null), "search", "b", out _, out _));
    }

    [Fact]
    public void Token_stays_valid_across_restarts_with_the_same_key_and_not_with_a_missing_key()
    {
        var first = Tokens();
        var token = first.Create("content", "issue:EX1:post:0", null, 42, "v");

        var restarted = Tokens(); // neuer Prozess, gleicher konfigurierter Schluessel
        Assert.True(restarted.TryValidate(token, "content", "issue:EX1:post:0", out var payload, out _));
        Assert.Equal(42, payload.Offset);

        var noKeyA = Tokens(o => o.ContinuationSigningKey = null);
        var noKeyB = Tokens(o => o.ContinuationSigningKey = null);
        Assert.False(noKeyB.TryValidate(noKeyA.Create("content", "x", null, 0, null), "content", "x", out _, out _)); // Prozess-Schluessel nie uebertragbar
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("zu-kurz", false)]
    [InlineData("@Microsoft.KeyVault(SecretUri=https://kv.vault.azure.net/secrets/x)", false)]
    [InlineData("__SET_VIA_KEYVAULT__0123456789012345678", false)]
    [InlineData("VALID32", true)]
    public void Signing_key_validity(string? key, bool valid) =>
        Assert.Equal(valid, ResponseLimitsOptionsValidator.IsValidKey(key == "VALID32" ? new string('a', 32) : key));

    [Fact]
    public void Startup_validation_requires_a_valid_key_outside_development_and_never_echoes_it()
    {
        const string secret = "@Microsoft.KeyVault(SecretUri=https://kv.vault.azure.net/secrets/geheim)";
        var prod = new ResponseLimitsOptionsValidator(new FakeEnv("Production"));
        var dev = new ResponseLimitsOptionsValidator(new FakeEnv("Development"));

        var missing = prod.Validate(null, new ResponseLimitsOptions());
        var unresolved = prod.Validate(null, new ResponseLimitsOptions { ContinuationSigningKey = secret });
        Assert.True(missing.Failed);
        Assert.True(unresolved.Failed);
        Assert.DoesNotContain("geheim", string.Join(" ", unresolved.Failures!));
        Assert.True(prod.Validate(null, new ResponseLimitsOptions { ContinuationSigningKey = new string('a', 32) }).Succeeded);
        Assert.True(dev.Validate(null, new ResponseLimitsOptions()).Succeeded);
        Assert.True(dev.Validate(null, new ResponseLimitsOptions { MaxResponseBytes = 2_000_000 }).Failed);
        Assert.True(dev.Validate(null, new ResponseLimitsOptions { MaxChunkBytes = 800_000 }).Failed);
    }

    private sealed class FakeEnv(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    // ---------- Validator ----------

    [Theory]
    [InlineData("EX226792", true)]
    [InlineData("a-b_1", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("EX1/posts", false)]
    [InlineData("https://evil", false)]
    [InlineData("", false)]
    public void Issue_id_is_validated(string id, bool valid) => Assert.Equal(valid, IssueInputValidator.TryParseIssueId(id, out _, out _));

    [Theory]
    [InlineData("issue:EX1:impact", true)]
    [InlineData("issue:EX1:details", true)]
    [InlineData("issue:EX1:post:12", true)]
    [InlineData("issue:EX1:post:-1", false)]
    [InlineData("issue:EX1:file:/etc/passwd", false)]
    [InlineData("https://graph.microsoft.com/x", false)]
    [InlineData("file:///c:/secrets", false)]
    [InlineData("issue:../x:impact", false)]
    public void Content_id_only_accepts_the_fixed_form(string id, bool valid) =>
        Assert.Equal(valid, IssueInputValidator.TryParseContentId(id, out _, out _, out _, out _));

    [Fact]
    public void Filter_values_reject_odata_injection_and_bad_enums()
    {
        Assert.False(IssueInputValidator.TryParseClassification("incident or true", out _, out _));
        Assert.False(IssueInputValidator.TryParseStatus("x' or 1 eq 1", out _, out _));
        Assert.False(IssueInputValidator.TryParseDate("nicht-datum", "startFrom", out _, out _));
        Assert.False(IssueInputValidator.TryNormalizeText(new string('a', 101), "service", out _, out _));
        Assert.False(IssueInputValidator.TryNormalizeText("a\nb", "service", out _, out _));
        Assert.False(IssueInputValidator.TryNormalizeMaxResults(101, out _, out _));
        Assert.False(IssueInputValidator.TryNormalizeMaxResults(0, out _, out _));
        Assert.True(IssueInputValidator.TryParseClassification("INCIDENT", out var c, out _) && c == "incident");
    }

    // ---------- Suche ----------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(100)]
    [InlineData(130)]
    public async Task Search_returns_all_items_across_pages_with_complete_flags(int count)
    {
        // 100 Eintraege je Graph-Seite (Graph-Default), mehrere Seiten bei >100.
        var all = Enumerable.Range(0, count).Select(i => Issue($"EX{i}")).ToList();
        var (svc, h) = Create((req, n) =>
        {
            var skip = req.RequestUri!.Query.Contains("skip=100") ? 100 : 0;
            var page = all.Skip(skip).Take(100);
            var next = skip == 0 && all.Count > 100 ? "https://graph.microsoft.com/v1.0/admin/serviceAnnouncement/issues?$skip=100" : null;
            return T.Task_(T.Page(string.Join(",", page), next));
        });

        using var doc = Parse(await svc.SearchAsync(Q(max: 100), null, CancellationToken.None));
        var root = doc.RootElement;

        var expected = Math.Min(count, 100);
        Assert.Equal(expected, root.GetProperty("itemCount").GetInt32());
        if (count > 100)
        {
            Assert.True(root.GetProperty("hasMore").GetBoolean());
            Assert.False(root.GetProperty("scanComplete").GetBoolean());
            Assert.True(root.GetProperty("continuationToken").GetString()!.Length > 10);
        }
        else
        {
            Assert.False(root.GetProperty("hasMore").GetBoolean());
            Assert.True(root.GetProperty("scanComplete").GetBoolean());
            Assert.False(root.GetProperty("truncated").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("continuationToken").ValueKind);
        }

        Assert.Equal(count == 0 ? "no_data" : "ok", root.GetProperty("status").GetString());
        Assert.Equal("Bearer graph-test-token", h.Handler.Authorizations[0]);
    }

    [Fact]
    public async Task Search_continues_through_all_pages_without_loss_or_duplicates()
    {
        var all = Enumerable.Range(0, 130).Select(i => Issue($"EX{i}")).ToList();
        var (svc, _) = Create((req, _) =>
        {
            var skip = req.RequestUri!.Query.Contains("skip=100") ? 100 : 0;
            var next = skip == 0 ? "https://graph.microsoft.com/v1.0/admin/serviceAnnouncement/issues?$skip=100" : null;
            return T.Task_(T.Page(string.Join(",", all.Skip(skip).Take(100)), next));
        });

        var ids = new List<string>();
        string? token = null;
        var calls = 0;
        do
        {
            using var doc = Parse(await svc.SearchAsync(Q(max: 40), token, CancellationToken.None));
            ids.AddRange(doc.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!));
            token = doc.RootElement.GetProperty("hasMore").GetBoolean() ? doc.RootElement.GetProperty("continuationToken").GetString() : null;
            calls++;
        } while (token is not null && calls < 10);

        Assert.Equal(130, ids.Count);
        Assert.Equal(130, ids.Distinct().Count());
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Search_token_cannot_be_used_with_changed_filters_or_tampered()
    {
        var all = Enumerable.Range(0, 50).Select(i => Issue($"EX{i}")).ToList();
        var (svc, h) = Create((_, _) => T.Task_(T.Page(string.Join(",", all))));

        using var first = Parse(await svc.SearchAsync(Q(max: 10), null, CancellationToken.None));
        var token = first.RootElement.GetProperty("continuationToken").GetString()!;
        var requestsBefore = h.Handler.Requests.Count;

        using var changed = Parse(await svc.SearchAsync(Q(max: 10, service: "Teams"), token, CancellationToken.None));
        using var tampered = Parse(await svc.SearchAsync(Q(max: 10), token[..^2] + "zz", CancellationToken.None));

        Assert.Equal("invalid_continuation_token", changed.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Equal("invalid_continuation_token", tampered.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Equal(requestsBefore, h.Handler.Requests.Count); // kein Graph-Aufruf mit ungueltigem Token
    }

    [Fact]
    public async Task Search_filters_apply_locally_and_graph_filter_is_built_from_validated_values()
    {
        var page = string.Join(",",
            Issue("EX1", "Exchange Online", false, "incident", "investigating", "2026-10-05T00:00:00Z", "Mailfluss"),
            Issue("EX2", "Exchange Online", true, "advisory", "serviceRestored", "2026-09-01T00:00:00Z", "Kalender"),
            Issue("TM1", "Microsoft Teams", false, "incident", "serviceDegradation", "2026-10-07T00:00:00Z", "Meetings"));
        var (svc, h) = Create((_, _) => T.Task_(T.Page(page)));

        async Task<string[]> Ids(IssueQuery q)
        {
            using var doc = Parse(await svc.SearchAsync(q, null, CancellationToken.None));
            return doc.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray();
        }

        Assert.Equal(["EX1", "EX2", "TM1"], await Ids(Q()));
        Assert.Equal(["EX1", "TM1"], await Ids(Q(cls: "incident")));
        Assert.Equal(["EX2"], await Ids(Q(status: "serviceRestored")));
        Assert.Equal(["EX1", "EX2"], await Ids(Q(service: "exchange online")));
        Assert.Equal(["EX2"], await Ids(Q(resolved: true)));
        Assert.Equal(["EX1"], await Ids(Q(text: "mailfluss")));
        Assert.Equal(["EX1", "TM1"], await Ids(Q(from: DateTimeOffset.Parse("2026-10-01T00:00:00Z"))));
        Assert.Equal(["EX2"], await Ids(Q(to: DateTimeOffset.Parse("2026-09-30T00:00:00Z"))));

        var filterUrl = h.Handler.Requests.First(r => r.RequestUri!.Query.Contains("filter")).RequestUri!.AbsoluteUri;
        Assert.Contains(Uri.EscapeDataString("service eq 'exchange online'"), filterUrl);

        // Injektion im Dienstnamen: Hochkomma wird verdoppelt, kein zusaetzlicher OData-Ausdruck
        await Ids(Q(service: "x' or isResolved eq true or service eq 'y"));
        var injected = h.Handler.Requests.Last().RequestUri!.AbsoluteUri;
        Assert.Contains(Uri.EscapeDataString("service eq 'x'' or isResolved eq true or service eq ''y'"), injected);
    }

    [Fact]
    public async Task Search_falls_back_to_local_filtering_when_graph_rejects_the_filter()
    {
        var (svc, _) = Create((req, _) => req.RequestUri!.Query.Contains("filter")
            ? T.Task_(T.Error(HttpStatusCode.BadRequest, "BadRequest", "filter"))
            : T.Task_(T.Page(Issue("EX1") + "," + Issue("TM1", "Microsoft Teams"))));

        using var doc = Parse(await svc.SearchAsync(Q(service: "Microsoft Teams"), null, CancellationToken.None));

        Assert.Equal("local", doc.RootElement.GetProperty("filterMode").GetString());
        Assert.Equal("TM1", Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("https://graph.microsoft.com.evil.example/v1.0/admin/serviceAnnouncement/issues?$skip=100")]
    [InlineData("http://graph.microsoft.com/v1.0/admin/serviceAnnouncement/issues?$skip=100")]
    [InlineData("https://graph.microsoft.com/v1.0/users?$skip=100")]
    [InlineData("https://user@graph.microsoft.com/v1.0/admin/serviceAnnouncement/issues?$skip=100")]
    [InlineData("https://graph.microsoft.com:8443/v1.0/admin/serviceAnnouncement/issues?$skip=100")]
    public async Task Search_rejects_manipulated_next_links_and_marks_truncated(string nextLink)
    {
        var (svc, h) = Create((_, _) => T.Task_(T.Page(Issue("EX1"), nextLink)));

        using var doc = Parse(await svc.SearchAsync(Q(), null, CancellationToken.None));

        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("scanComplete").GetBoolean());
        Assert.Contains("nextLink", doc.RootElement.GetProperty("truncationReason").GetString());
        Assert.Single(h.Handler.Requests);
    }

    [Fact]
    public async Task Search_is_bounded_by_MaxPages_and_stays_resumable()
    {
        var (svc, h) = Create((req, n) => T.Task_(T.Page(Issue($"X{n}"), $"https://graph.microsoft.com/v1.0/admin/serviceAnnouncement/issues?$skip={n * 100}")),
            health: o => o.MaxPages = 3);

        using var doc = Parse(await svc.SearchAsync(Q(text: "gibtesnicht"), null, CancellationToken.None));

        Assert.Equal(3, h.Handler.Requests.Count);
        Assert.True(doc.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("scanComplete").GetBoolean());
        Assert.Contains("MaxPages", doc.RootElement.GetProperty("truncationReason").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "forbidden")]
    [InlineData(HttpStatusCode.NotFound, "not_found")]
    public async Task Search_reports_graph_errors_structured(HttpStatusCode code, string expected)
    {
        var (svc, _) = Create((_, _) => T.Task_(T.Error(code, "x", "y")));
        using var doc = Parse(await svc.SearchAsync(Q(), null, CancellationToken.None));
        Assert.Equal("error", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(expected, doc.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Search_retries_429_and_503_then_succeeds()
    {
        var (svc, h) = Create((_, n) => n switch
        {
            1 => T.Task_(T.Throttled(TimeSpan.FromSeconds(1))),
            2 => T.Task_(T.Status(HttpStatusCode.ServiceUnavailable)),
            _ => T.Task_(T.Page(Issue("EX1")))
        });

        using var doc = Parse(await svc.SearchAsync(Q(), null, CancellationToken.None));

        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task Search_honours_cancellation()
    {
        var (svc, _) = Create((_, _) => new TaskCompletionSource<HttpResponseMessage>().Task); // Antwort kommt nie
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.SearchAsync(Q(), null, cts.Token));
    }

    [Fact]
    public async Task Search_summary_is_small_and_omits_post_bodies_but_lists_content_refs()
    {
        var big = new string('x', 200_000);
        var longImpact = new string('i', 2_000);
        var (svc, _) = Create((_, _) => T.Task_(T.Page(string.Join(",", Enumerable.Range(0, 20).Select(i => Issue($"EX{i}", impact: longImpact, posts: [big, big]))))));

        var json = await svc.SearchAsync(Q(max: 20), null, CancellationToken.None);

        Assert.True(ResponseSizeGuard.EscapedSize(json) < 100_000);
        using var doc = Parse(json);
        var first = doc.RootElement.GetProperty("items")[0];
        Assert.DoesNotContain("xxxxxxxx", json);
        Assert.True(first.GetProperty("impactDescriptionTruncated").GetBoolean());
        Assert.Equal(2, first.GetProperty("postCount").GetInt32());
        var refs = first.GetProperty("contentRefs").EnumerateArray().Select(r => r.GetString()).ToArray();
        Assert.Contains("issue:EX0:post:1", refs);
        Assert.Contains("issue:EX0:impact", refs);
    }

    // ---------- Einzelabruf ----------

    [Fact]
    public async Task Get_issue_full_inlines_small_content_and_is_partial_with_refs_when_too_large()
    {
        var (small, _) = Create((_, _) => T.Task_(T.Json(Issue("EX1", posts: ["<p>kurz</p>"]))));
        using (var doc = Parse(await small.GetIssueAsync("EX1", "full", CancellationToken.None)))
        {
            Assert.False(doc.RootElement.GetProperty("isPartial").GetBoolean());
            Assert.Contains("kurz", doc.RootElement.GetProperty("posts")[0].GetProperty("description").GetProperty("content").GetString());
            Assert.True(doc.RootElement.GetProperty("contentVersion").GetString()!.Length >= 8);
        }

        var (large, _) = Create((_, _) => T.Task_(T.Json(Issue("EX2", posts: [new string('z', 900_000)]))));
        var json = await large.GetIssueAsync("EX2", "full", CancellationToken.None);
        Assert.True(ResponseSizeGuard.EscapedSize(json) <= 700_000);
        using var big = Parse(json);
        Assert.True(big.RootElement.GetProperty("isPartial").GetBoolean());
        Assert.True(big.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal("issue:EX2:post:0", big.RootElement.GetProperty("posts")[0].GetProperty("contentId").GetString());
        Assert.Equal(900_000, big.RootElement.GetProperty("posts")[0].GetProperty("contentChars").GetInt32());
    }

    // ---------- Content (verlustfrei) ----------

    private static async Task<(string Text, int Chunks, List<string> Versions)> ReadAll(ServiceHealthIssueService svc, string contentId)
    {
        var sb = new StringBuilder();
        var versions = new List<string>();
        string? token = null;
        var chunks = 0;
        do
        {
            var json = await svc.GetContentAsync(contentId, token, CancellationToken.None);
            Assert.True(ResponseSizeGuard.EscapedSize(json) <= 700_000, "Chunk ueberschreitet das Limit");
            using var doc = Parse(json);
            var root = doc.RootElement;
            Assert.Equal("ok", root.GetProperty("status").GetString());
            Assert.False(root.GetProperty("truncated").GetBoolean());
            sb.Append(root.GetProperty("text").GetString());
            versions.Add(root.GetProperty("contentVersion").GetString()!);
            token = root.GetProperty("hasMore").GetBoolean() ? root.GetProperty("continuationToken").GetString() : null;
            Assert.Equal(token is not null, root.GetProperty("isPartial").GetBoolean());
            chunks++;
        } while (token is not null && chunks < 200);

        return (sb.ToString(), chunks, versions);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(300_000)]
    [InlineData(1_500_000)]
    public async Task Content_is_returned_losslessly_in_chunks_below_the_limit(int length)
    {
        var original = new string('a', length);
        var (svc, _) = Create((_, _) => T.Task_(T.Json(Issue("EX1", posts: [original]))));

        var (text, chunks, versions) = await ReadAll(svc, "issue:EX1:post:0");

        Assert.Equal(original, text);
        Assert.Single(versions.Distinct());
        if (length > 600_000) Assert.True(chunks > 1);
    }

    [Fact]
    public async Task Content_with_unicode_html_and_emoji_is_lossless_and_never_splits_surrogates()
    {
        var unit = "<p class=\"x\">Größe äöü 日本語 😀🎉 \"zitat\" \\pfad\\ & <b>fett</b></p>\n";
        var original = string.Concat(Enumerable.Repeat(unit, 12_000));
        var (svc, _) = Create((_, _) => T.Task_(T.Json(Issue("EX1", posts: [original]))), limits: o => { o.MaxResponseBytes = 200_000; o.MaxChunkBytes = 100_000; });

        var (text, chunks, _) = await ReadAll(svc, "issue:EX1:post:0");

        Assert.Equal(original, text);
        Assert.True(chunks > 3);
        Assert.DoesNotContain('\uFFFD', text);
    }

    [Fact]
    public async Task Content_impact_and_details_refs_work_and_unknown_refs_are_not_found()
    {
        var (svc, _) = Create((_, _) => T.Task_(T.Json(Issue("EX1", impact: "Auswirkung lang", posts: ["p"]))));

        var (impact, _, _) = await ReadAll(svc, "issue:EX1:impact");
        var (details, _, _) = await ReadAll(svc, "issue:EX1:details");
        Assert.Equal("Auswirkung lang", impact);
        Assert.Contains("NotifyInApp", details);

        using var missing = Parse(await svc.GetContentAsync("issue:EX1:post:5", null, CancellationToken.None));
        Assert.Equal("not_found", missing.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Content_change_between_chunks_is_detected()
    {
        var version = 0;
        var (svc, _) = Create((_, _) => T.Task_(T.Json(Issue("EX1", posts: [new string(version == 0 ? 'a' : 'b', 1_500_000)]))));

        using var first = Parse(await svc.GetContentAsync("issue:EX1:post:0", null, CancellationToken.None));
        var token = first.RootElement.GetProperty("continuationToken").GetString();
        version = 1;

        using var second = Parse(await svc.GetContentAsync("issue:EX1:post:0", token, CancellationToken.None));

        Assert.Equal("content_changed", second.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Content_rejects_foreign_tokens_and_unsafe_ids_without_calling_graph()
    {
        var (svc, h) = Create((_, _) => T.Task_(T.Json(Issue("EX1", posts: [new string('a', 1_500_000)]))));
        using var first = Parse(await svc.GetContentAsync("issue:EX1:post:0", null, CancellationToken.None));
        var token = first.RootElement.GetProperty("continuationToken").GetString();
        var before = h.Handler.Requests.Count;

        foreach (var (id, tk) in new[] { ("issue:EX2:post:0", token), ("https://graph.microsoft.com/v1.0/users", null), ("file:///c:/x", null), ("issue:EX1:post:0", "forged.token") })
        {
            using var doc = Parse(await svc.GetContentAsync(id, tk, CancellationToken.None));
            Assert.Equal("error", doc.RootElement.GetProperty("status").GetString());
        }

        Assert.Equal(before, h.Handler.Requests.Count);
    }
}
