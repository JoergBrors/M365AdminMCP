namespace ApiServer.Auth;

public static class PolicyNames
{
    /// <summary>
    /// Erlaubt Zugriff entweder per App Role "Tasks.ReadWrite.All" (App-only)
    /// oder per Delegated Scope "Tasks.ReadWrite" (Nutzerkontext).
    /// </summary>
    public const string TasksReadWrite = "TasksReadWrite";
}
