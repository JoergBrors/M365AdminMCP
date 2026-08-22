using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using ApiServer.Auth;
using ApiServer.Services;
using Microsoft.OpenApi.Models;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// --- Authentication: validiert Entra-ID-Tokens (JWT Bearer) ---
// Unterstützt sowohl App-only-Tokens (Claim "roles") als auch Delegated-Tokens (Claim "scp").
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
{
    var apiClientId = builder.Configuration["AzureAd:ClientId"];
    var apiAudience = builder.Configuration["AzureAd:Audience"];

    options.TokenValidationParameters.ValidAudiences = new[]
    {
        apiAudience,
        apiClientId
    }.Where(audience => !string.IsNullOrWhiteSpace(audience));
});

builder.Services.AddAuthorization(options =>
{
    // Policy, die entweder eine App-Rolle (App-only) ODER einen Delegated Scope akzeptiert.
    options.AddPolicy(PolicyNames.TasksReadWrite, policy =>
        policy.RequireAssertion(context =>
            context.User.Claims.Any(c =>
                (c.Type == "roles" || c.Type == ClaimTypes.Role) &&
                c.Value == "Tasks.ReadWrite.All") ||
            context.User.Claims.Any(c =>
                (c.Type == "scp" || c.Type == "http://schemas.microsoft.com/identity/claims/scope") &&
                c.Value.Split(' ').Contains("Tasks.ReadWrite"))
        ));
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.Configure<GraphAuthOptions>(builder.Configuration.GetSection("GraphAuth"));
builder.Services.AddSingleton<GraphTokenService>();
builder.Services.AddSingleton<GraphApiClient>();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("GraphNoRedirect")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ApiServer",
        Version = "v1"
    });

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
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/swagger"))
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers.Expires = "0";
                return Task.CompletedTask;
            });
        }

        await next();
    });

    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "ApiServer v1");

        var swaggerClientId = builder.Configuration["SwaggerOAuth:ClientId"];
        var swaggerScope = builder.Configuration["SwaggerOAuth:Scope"];
        if (!string.IsNullOrWhiteSpace(swaggerClientId))
        {
            options.OAuthClientId(swaggerClientId);
            options.OAuthAppName("ApiServer Swagger");
            options.OAuthUsePkce();
            options.OAuthScopeSeparator(" ");
            options.OAuthAdditionalQueryStringParams(new Dictionary<string, string>
            {
                ["prompt"] = "select_account"
            });
        }
        if (!string.IsNullOrWhiteSpace(swaggerScope))
        {
            options.OAuthScopes(swaggerScope);
        }
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();
