using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;
using ApiServer.Auth;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// --- Authentication: validiert Entra-ID-Tokens (JWT Bearer) ---
// Unterstützt sowohl App-only-Tokens (Claim "roles") als auch Delegated-Tokens (Claim "scp").
builder.Services
    .AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options =>
{
    // Policy, die entweder eine App-Rolle (App-only) ODER einen Delegated Scope akzeptiert.
    options.AddPolicy(PolicyNames.TasksReadWrite, policy =>
        policy.RequireAssertion(context =>
            context.User.HasClaim(c => c.Type == "roles" && c.Value == "Tasks.ReadWrite.All") ||
            context.User.Claims.Any(c =>
                (c.Type == "scp" || c.Type == "http://schemas.microsoft.com/identity/claims/scope") &&
                c.Value.Split(' ').Contains("Tasks.ReadWrite"))
        ));
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var tenantId = builder.Configuration["AzureAd:TenantId"];
    var swaggerScope = builder.Configuration["SwaggerOAuth:Scope"];

    if (!string.IsNullOrWhiteSpace(tenantId) && !string.IsNullOrWhiteSpace(swaggerScope))
    {
        options.AddSecurityDefinition("oauth2", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Flows = new OpenApiOAuthFlows
            {
                AuthorizationCode = new OpenApiOAuthFlow
                {
                    AuthorizationUrl = new Uri($"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize"),
                    TokenUrl = new Uri($"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token"),
                    Scopes = new Dictionary<string, string>
                    {
                        [swaggerScope] = "Read/write tasks as the signed-in user"
                    }
                }
            }
        });

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "oauth2"
                    }
                },
                new[] { swaggerScope }
            }
        });
    }
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        var swaggerClientId = builder.Configuration["SwaggerOAuth:ClientId"];
        if (!string.IsNullOrWhiteSpace(swaggerClientId))
        {
            options.OAuthClientId(swaggerClientId);
            options.OAuthUsePkce();
            options.OAuthScopeSeparator(" ");
        }
    });
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();
