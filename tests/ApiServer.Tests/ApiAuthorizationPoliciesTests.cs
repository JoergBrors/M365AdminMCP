using System.Security.Claims;
using ApiServer.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace ApiServer.Tests;

/// <summary>Regressionstests fuer F-016: Daten-Controller duerfen nicht per delegiertem Nutzer-Scope erreichbar sein.</summary>
public class ApiAuthorizationPoliciesTests
{
    private static IAuthorizationService Service()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(ApiAuthorizationPolicies.Configure);
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal User(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    private static ClaimsPrincipal AppOnly() => User(new Claim("roles", "Tasks.ReadWrite.All"));

    private static ClaimsPrincipal DelegatedUser(string scope = "Tasks.ReadWrite") =>
        User(new Claim("scp", scope), new Claim("oid", "00000000-0000-0000-0000-000000000001"));

    [Fact]
    public async Task App_only_token_with_the_app_role_may_read_data() =>
        Assert.True((await Service().AuthorizeAsync(AppOnly(), null, PolicyNames.DataAccess)).Succeeded);

    [Fact]
    public async Task Delegated_user_token_with_tasks_scope_may_NOT_read_data() =>
        Assert.False((await Service().AuthorizeAsync(DelegatedUser(), null, PolicyNames.DataAccess)).Succeeded);

    [Fact]
    public async Task Role_claim_mapped_to_ClaimTypes_Role_is_accepted()
    {
        var user = User(new Claim(ClaimTypes.Role, "Tasks.ReadWrite.All"));
        Assert.True((await Service().AuthorizeAsync(user, null, PolicyNames.DataAccess)).Succeeded);
    }

    [Theory]
    [InlineData("Mcp.Access")]
    [InlineData("tasks.readwrite.all")]
    [InlineData("Tasks.Read")]
    public async Task Other_roles_do_not_grant_data_access(string role) =>
        Assert.False((await Service().AuthorizeAsync(User(new Claim("roles", role)), null, PolicyNames.DataAccess)).Succeeded);

    [Fact]
    public async Task Anonymous_is_rejected() =>
        Assert.False((await Service().AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, PolicyNames.DataAccess)).Succeeded);

    [Fact]
    public async Task Tasks_demo_policy_still_accepts_role_or_scope()
    {
        var svc = Service();
        Assert.True((await svc.AuthorizeAsync(AppOnly(), null, PolicyNames.TasksReadWrite)).Succeeded);
        Assert.True((await svc.AuthorizeAsync(DelegatedUser("openid Tasks.ReadWrite"), null, PolicyNames.TasksReadWrite)).Succeeded);
        Assert.False((await svc.AuthorizeAsync(DelegatedUser("Other.Scope"), null, PolicyNames.TasksReadWrite)).Succeeded);
    }
}
