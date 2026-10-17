namespace ApiServer.Auth;

public static class PolicyNames
{
    /// <summary>
    /// Erlaubt Zugriff entweder per App Role "Tasks.ReadWrite.All" (App-only)
    /// oder per Delegated Scope "Tasks.ReadWrite" (Nutzerkontext). Nur fuer den Task-Demo-Controller.
    /// </summary>
    public const string TasksReadWrite = "TasksReadWrite";

    /// <summary>
    /// Daten-Controller (Status, Messages, Reports, Health): nur App-Rolle "Tasks.ReadWrite.All", kein Delegated Scope.
    /// Siehe <see cref="ApiAuthorizationPolicies"/>.
    /// </summary>
    public const string DataAccess = "DataAccess";
}
