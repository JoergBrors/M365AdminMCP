namespace ApiServer.Health.Models;

/// <summary>Einheitliches Fehlerobjekt. Enthaelt nie Tokens oder vorautorisierte URLs.</summary>
public sealed record HealthError(string Code, string Message, int? HttpStatus = null);

/// <summary>
/// Einheitliche Antwortstruktur aller Health-Tools. "status" beschreibt den ABRUF der Daten, nicht die Gesundheit eines Dienstes:
/// ok | no_data | partial | error | disabled.
/// </summary>
public sealed record HealthResponse<T>(
    string Source,
    DateTime QueriedAtUtc,
    string? TenantId,
    string? SubscriptionId,
    string Status,
    IReadOnlyList<T> Items,
    int ItemCount,
    IReadOnlyList<HealthError> Errors,
    bool IsPartial,
    bool Truncated,
    string? TruncationReason,
    object? Aggregates = null)
{
    public static HealthResponse<T> Success(string source, DateTime now, string? tenantId, string? subscriptionId,
        IReadOnlyList<T> items, bool truncated = false, string? truncationReason = null, object? aggregates = null,
        IReadOnlyList<HealthError>? warnings = null) =>
        new(source, now, tenantId, subscriptionId, items.Count == 0 ? "no_data" : "ok", items, items.Count,
            warnings ?? [], truncated, truncated, truncationReason, aggregates);

    public static HealthResponse<T> Failure(string source, DateTime now, string? tenantId, string? subscriptionId,
        string code, string message, int? httpStatus = null, string status = "error") =>
        new(source, now, tenantId, subscriptionId, status, [], 0, [new HealthError(code, message, httpStatus)], false, false, null);

    public static HealthResponse<T> Disabled(string source, DateTime now, string? tenantId, string? subscriptionId, string reason) =>
        new(source, now, tenantId, subscriptionId, "disabled", [], 0, [new HealthError("disabled", reason)], false, false, null);
}

// ---- Microsoft Graph (Service Communications) ----

public sealed record ServiceHealthItem(string? Id, string? Service, string? Status, bool IsOperational);

public sealed record ServiceIncidentItem(
    string? Id,
    string? Title,
    string? Service,
    string? Status,
    string? Classification,
    string? Origin,
    bool IsResolved,
    DateTimeOffset? StartDateTime,
    DateTimeOffset? EndDateTime,
    DateTimeOffset? LastModifiedDateTime,
    string? Feature,
    string? FeatureGroup,
    string? ImpactDescription);

public sealed record ServiceMessageItem(
    string? Id,
    string? Title,
    IReadOnlyList<string> Services,
    string? Category,
    string? Severity,
    DateTimeOffset? StartDateTime,
    DateTimeOffset? EndDateTime,
    DateTimeOffset? LastModifiedDateTime,
    bool IsMajorChange,
    bool ActionRequired,
    DateTimeOffset? ActionRequiredByDateTime,
    IReadOnlyList<string> Tags,
    string? BodyPreview);

// ---- Azure Resource Health ----

public sealed record AzureImpactedService(string? Service, IReadOnlyList<string> Regions);

public sealed record AzureHealthEventItem(
    string? Id,
    string? EventType,
    string? EventSource,
    string? Status,
    string? Level,
    string? Title,
    string? Summary,
    DateTimeOffset? ImpactStartTime,
    DateTimeOffset? ImpactMitigationTime,
    DateTimeOffset? LastUpdateTime,
    int? Priority,
    bool IsEventSensitive,
    IReadOnlyList<AzureImpactedService> ImpactedServices,
    IReadOnlyList<string> Tags);

public sealed record ResourceAvailabilityItem(
    string? ResourceId,
    string? Location,
    string AvailabilityState,
    string? Title,
    string? Summary,
    string? DetailedStatus,
    string? ReasonType,
    DateTimeOffset? OccurredTime,
    DateTimeOffset? ReportedTime,
    DateTimeOffset? ResolutionEta,
    string? HealthEventId);

public sealed record AvailabilityAggregates(int Available, int Unavailable, int Degraded, int Unknown, int Other, bool BasedOnCompleteData);

// ---- Zusammenfassung ----

public sealed record HealthSourceStatus(string Name, string Status, DateTime QueriedAtUtc, int ItemCount, bool IsPartial, IReadOnlyList<HealthError> Errors);

public sealed record MicrosoftHealthSummary(
    DateTime QueriedAtUtc,
    string? TenantId,
    string Status,
    bool IsPartial,
    IReadOnlyList<HealthSourceStatus> Sources,
    HealthResponse<ServiceHealthItem> IntuneServiceHealth,
    HealthResponse<ServiceIncidentItem> ActiveIntuneIncidents,
    HealthResponse<ServiceMessageItem> IntuneMessages,
    IReadOnlyList<HealthResponse<AzureHealthEventItem>> AzureServiceHealth);
