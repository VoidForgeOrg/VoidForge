using JasperFx;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Voidforge.Api.Auth;
using Voidforge.Api.Documents;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Marten")
    ?? throw new InvalidOperationException("Connection string 'Marten' is required.");

builder.Host.ApplyJasperFxExtensions();

builder.Services.AddMarten(opts =>
{
    opts.Connection(connectionString);
    opts.DatabaseSchemaName = "voidforge";
    opts.Schema.For<ApiKey>().UniqueIndex(x => x.HashedKey);
})
.UseLightweightSessions()
.IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    opts.Policies.AutoApplyTransactions();
    opts.Durability.Mode = DurabilityMode.Solo;
});

builder.Services.AddAuthentication(ApiKeyAuthenticationDefaults.AuthenticationScheme)
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationDefaults.AuthenticationScheme, _ => { });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

builder.Services.AddWolverineHttp();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks().AddNpgSql(connectionString);

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapWolverineEndpoints();

return await app.RunJasperFxCommands(args);

public partial class Program;
