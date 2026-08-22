namespace ApiServer.Auth;

public class GraphAuthOptions
{
    public bool UseManagedIdentity { get; set; }
    public string? ManagedIdentityClientId { get; set; }
}
