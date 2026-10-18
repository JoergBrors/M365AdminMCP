using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace McpServer.Tools;

/// <summary>Zentrale Tool-Registrierung (von Program.cs und den Schema-Tests gemeinsam genutzt).</summary>
public static class McpToolCatalog
{
    public static IMcpServerBuilder AddM365Tools(this IMcpServerBuilder builder) => builder
        .WithTools<TasksTool>()
        .WithTools<M365StatusTool>()
        .WithTools<M365MessagesTool>()
        .WithTools<M365AdoptionTool>()
        .WithTools<M365AdoptionCatalogTool>()
        .WithTools<MicrosoftHealthTools>()
        // Neue Tools: Schema ohne Null-Typ-Arrays (siehe SchemaCompatibleTools).
        .WithTools(SchemaCompatibleTools.For<ServiceHealthIssueTools>());
}
