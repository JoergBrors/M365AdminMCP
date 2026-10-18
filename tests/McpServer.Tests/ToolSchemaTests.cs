using System.Text.Json;
using McpServer.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace McpServer.Tests;

/// <summary>
/// Prueft die Input-Schemas, die tools/list tatsaechlich veroeffentlicht (McpServerTool.ProtocolTool = Quelle der Antwort),
/// nicht die C#-Signaturen. Q-API-2: keine Typ-Arrays mit "null" in den neuen Tools.
/// </summary>
public class ToolSchemaTests
{
    private static readonly string[] NewTools = ["search_service_health_issues", "get_service_health_issue", "get_service_health_issue_content"];

    // Bestehende Tools mit Null-Typ-Arrays (nicht Teil von Q-API-2, bewusst unveraendert). Neue Tools duerfen hier nie erscheinen.
    private static readonly string[] KnownLegacyNullTypes =
    [
        "azure_service_health", "azure_subscription_resource_health", "get_adoption_report", "get_message_center_messages",
        "intune_service_incidents", "list_adoption_reports", "microsoft_service_messages"
    ];

    private static Dictionary<string, JsonElement> PublishedSchemas()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddMcpServer().AddM365Tools().Services
            .BuildServiceProvider();

        var tools = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection
            ?? throw new InvalidOperationException("Keine Tools registriert.");

        // Wie tools/list: Tool-Objekt serialisieren (Schema als JSON), dann inputSchema lesen.
        return tools.ToDictionary(
            t => t.ProtocolTool.Name,
            t => JsonSerializer.SerializeToElement(t.ProtocolTool.InputSchema));
    }

    private static IEnumerable<JsonElement> Walk(JsonElement e)
    {
        yield return e;
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
                foreach (var c in Walk(p.Value)) yield return c;
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var i in e.EnumerateArray())
                foreach (var c in Walk(i)) yield return c;
    }

    private static bool HasNullTypeArray(JsonElement schema) =>
        Walk(schema).Any(n => n.ValueKind == JsonValueKind.Object && n.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Array);

    [Fact]
    public void New_tools_are_published_under_their_names()
    {
        var schemas = PublishedSchemas();
        foreach (var name in NewTools) Assert.True(schemas.ContainsKey(name), name);
        Assert.True(schemas.ContainsKey("get_service_health_issues")); // Alt-Tool unveraendert vorhanden
    }

    [Fact]
    public void New_tools_publish_no_type_arrays_and_no_null_defaults()
    {
        var schemas = PublishedSchemas();
        foreach (var name in NewTools)
        {
            Assert.False(HasNullTypeArray(schemas[name]), $"{name}: Typ-Array im veroeffentlichten Schema");
            Assert.DoesNotContain("\"default\":null", schemas[name].GetRawText());
        }
    }

    [Fact]
    public void Optional_parameters_keep_single_types_and_are_not_required()
    {
        var search = PublishedSchemas()["search_service_health_issues"];
        var props = search.GetProperty("properties");

        Assert.Equal("boolean", props.GetProperty("isResolved").GetProperty("type").GetString());
        Assert.Equal("integer", props.GetProperty("maxResults").GetProperty("type").GetString());
        foreach (var p in new[] { "service", "classification", "status", "startFrom", "startTo", "searchText", "continuationToken" })
            Assert.Equal("string", props.GetProperty(p).GetProperty("type").GetString());

        Assert.False(search.TryGetProperty("required", out var required) && required.GetArrayLength() > 0, "alle Filter sind optional");

        var issue = PublishedSchemas()["get_service_health_issue"];
        Assert.Contains("id", issue.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain("projection", issue.GetProperty("required").EnumerateArray().Select(x => x.GetString()));

        var content = PublishedSchemas()["get_service_health_issue_content"];
        Assert.Contains("contentId", content.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void Catalog_only_contains_the_documented_legacy_null_type_tools()
    {
        var withNullTypes = PublishedSchemas().Where(kv => HasNullTypeArray(kv.Value)).Select(kv => kv.Key).Order().ToArray();
        Assert.Equal(KnownLegacyNullTypes.Order().ToArray(), withNullTypes);
    }

    [Fact]
    public void Legacy_tool_schemas_are_unchanged_in_name_and_required_parameters()
    {
        var schemas = PublishedSchemas();
        Assert.Equal("serviceHealthId", Assert.Single(schemas["get_service_health_detail"].GetProperty("required").EnumerateArray()).GetString());
        Assert.False(schemas["get_service_health_issues"].TryGetProperty("required", out var r) && r.GetArrayLength() > 0);
        Assert.Equal(20, schemas.Count);
    }
}
