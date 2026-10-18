using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace McpServer.Tools;

/// <summary>
/// Registriert die [McpServerTool]-Methoden einer Klasse mit einem Schema-Transform nur fuer DIESE Klasse.
/// Der SDK-Generator veroeffentlicht optionale Parameter als {"type":["string","null"],"default":null}. Das ist gueltiges JSON Schema,
/// wird aber von einigen MCP-Clients/Function-Calling-Anbietern nicht unterstuetzt (Inspector-Warnung). Der Transform macht daraus
/// {"type":"string"} (ohne "default": null); ein Parameter ist optional, weil er nicht in "required" steht. Die C#-Signatur bleibt
/// unveraendert (bool?/int?), damit fehlend/null/false/0 serverseitig weiter unterscheidbar sind. Andere Tools sind nicht betroffen.
/// </summary>
public static class SchemaCompatibleTools
{
    public static IEnumerable<McpServerTool> For<TTool>() where TTool : class
    {
        foreach (var method in typeof(TTool).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is null)
            {
                continue;
            }

            yield return McpServerTool.Create(
                method,
                request => ActivatorUtilities.CreateInstance<TTool>(request.Services!),
                new McpServerToolCreateOptions
                {
                    SchemaCreateOptions = new AIJsonSchemaCreateOptions { TransformSchemaNode = (_, node) => RemoveNullType(node) }
                });
        }
    }

    /// <summary>Entfernt "null" aus einem Typ-Array eines Knotens und das daraus entstehende "default": null.</summary>
    public static JsonNode RemoveNullType(JsonNode node)
    {
        if (node is not JsonObject obj || obj["type"] is not JsonArray types)
        {
            return node;
        }

        var remaining = types.Select(t => t?.GetValue<string>()).Where(t => t is not null && t != "null").Cast<string>().ToList();
        if (remaining.Count == types.Count || remaining.Count == 0)
        {
            return node;
        }

        obj["type"] = remaining.Count == 1 ? JsonValue.Create(remaining[0]) : new JsonArray(remaining.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        if (obj["default"] is null && obj.ContainsKey("default"))
        {
            obj.Remove("default");
        }

        return obj;
    }
}
