using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;
using ApiServer.Auth;

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
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();
