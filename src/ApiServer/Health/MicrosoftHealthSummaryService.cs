using ApiServer.Health.Models;
using Microsoft.Extensions.Options;

namespace ApiServer.Health;

/// <summary>
/// Aggregiert Intune Health, aktive Intune Incidents, Intune-Message-Center-Meldungen und Azure Service Health der freigegebenen
/// Subscriptions. Teilfehler sind isoliert: faellt eine Quelle aus, liefern die anderen weiterhin Daten. Es wird KEINE globale
/// Gesundheitsbewertung berechnet; "status" beschreibt nur den Datenabruf.
/// </summary>
public class MicrosoftHealthSummaryService
{
    private readonly MicrosoftServiceHealthClient _graph;
    private readonly AzureResourceHealthClient _azure;
    private readonly MicrosoftHealthOptions _options;
    private readonly TimeProvider _time;

    public MicrosoftHealthSummaryService(
        MicrosoftServiceHealthClient graph,
        AzureResourceHealthClient azure,
        IOptions<MicrosoftHealthOptions> options,
        TimeProvider? timeProvider = null)
    {
        _graph = graph;
        _azure = azure;
        _options = options.Value;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<MicrosoftHealthSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var tenant = string.IsNullOrWhiteSpace(_options.TenantId) ? null : _options.TenantId;

        var healthTask = Safe("graph.healthOverviews", now, tenant, null, () => _graph.GetIntuneHealthAsync(cancellationToken));
        var incidentsTask = Safe("graph.issues", now, tenant, null, () => _graph.GetIntuneIncidentsAsync(true, 25, cancellationToken));
        var messagesTask = Safe("graph.messages", now, tenant, null,
            () => _graph.GetMessagesAsync(true, now.AddDays(-30), null, 25, cancellationToken));

        var azureTasks = new List<Task<HealthResponse<AzureHealthEventItem>>>();
        var allowed = _azure.AllowedSubscriptions;
        if (!_options.Enabled || !_options.EnableAzure || allowed.Count == 0)
        {
            azureTasks.Add(Task.FromResult(HealthResponse<AzureHealthEventItem>.Disabled("arm.events", now, tenant, null,
                "Azure Service Health ist deaktiviert oder es sind keine Subscriptions freigegeben (MicrosoftHealth:AllowedSubscriptionIds).")));
        }
        else
        {
            foreach (var subscriptionId in allowed)
            {
                var id = subscriptionId;
                azureTasks.Add(Safe("arm.events", now, tenant, id, () => _azure.GetServiceHealthEventsAsync(id, now.AddDays(-3), 25, cancellationToken)));
            }
        }

        await Task.WhenAll(new Task[] { healthTask, incidentsTask, messagesTask }.Concat(azureTasks));

        var health = healthTask.Result;
        var incidents = incidentsTask.Result;
        var messages = messagesTask.Result;
        var azureResults = azureTasks.Select(t => t.Result).ToList();

        var sources = new List<HealthSourceStatus>
        {
            ToSource("intune_service_health", health),
            ToSource("intune_active_incidents", incidents),
            ToSource("intune_messages", messages)
        };
        sources.AddRange(azureResults.Select(r => ToSource(r.SubscriptionId is null ? "azure_service_health" : $"azure_service_health:{r.SubscriptionId}", r)));

        var active = sources.Where(s => s.Status != "disabled").ToList();
        var failed = active.Count(s => s.Status == "error");
        var overall = active.Count == 0 ? "disabled"
            : failed == 0 ? "ok"
            : failed == active.Count ? "error"
            : "partial";
        var isPartial = failed > 0 && failed < active.Count || active.Any(s => s.IsPartial);

        return new MicrosoftHealthSummary(now, tenant, overall, isPartial, sources, health, incidents, messages, azureResults);
    }

    private static async Task<HealthResponse<T>> Safe<T>(
        string source, DateTime now, string? tenant, string? subscriptionId, Func<Task<HealthResponse<T>>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unerwartete Fehler einer Quelle duerfen die uebrigen nicht beeintraechtigen. Nur der Typ wird gemeldet (keine Details).
            return HealthResponse<T>.Failure(source, now, tenant, subscriptionId, "unexpected_error", $"Unerwarteter Fehler beim Abruf ({ex.GetType().Name}).");
        }
    }

    private static HealthSourceStatus ToSource<T>(string name, HealthResponse<T> response) =>
        new(name, response.Status, response.QueriedAtUtc, response.ItemCount, response.IsPartial || response.Truncated, response.Errors);
}
