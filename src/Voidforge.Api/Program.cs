using JasperFx;
using JasperFx.Events;
using Marten;
using Marten.Events.Projections;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Voidforge.Api.Auth;
using Voidforge.Api.Balance;
using Voidforge.Api.Documents;
using Voidforge.Api.Domain;
using Voidforge.Api.Http;
using Voidforge.Api.OpenApi;
using Voidforge.Api.Travel;
using Voidforge.Api.WorldGeneration;
using Wolverine;
using Wolverine.ErrorHandling;
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
    opts.Events.AppendMode = EventAppendMode.Quick;
    opts.Events.UseIdentityMapForAggregates = true;
    opts.Projections.Snapshot<Player>(SnapshotLifecycle.Inline);
    opts.Projections.Snapshot<Planet>(SnapshotLifecycle.Inline);
    opts.Projections.Snapshot<Fleet>(SnapshotLifecycle.Inline);
    opts.Schema.For<Player>().UniqueIndex(x => x.Name);
    opts.Schema.For<WorldSeedMarker>();
})
.UseLightweightSessions()
.IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    opts.Policies.AutoApplyTransactions();
    opts.Durability.Mode = DurabilityMode.Solo;

    // Same-planet-stream concurrency (#39). Multiple paths append to one Planet stream: parallel
    // scheduled completions, and player commands (HTTP) landing in the same window. Marten
    // optimistic concurrency (FetchForWriting at every append site) makes the loser fail with a
    // ConcurrencyException instead of racing to a duplicate stream version. Completion handlers
    // reload the committed snapshot and re-run on retry; the pure aggregate methods are idempotent
    // (validate-on-arrival), so re-application is a safe no-op — a collided completion is retried
    // to success and never dropped. The backoff ladder is generous so exhausting it (→ dead-letter)
    // is effectively impossible for these rare, transient collisions at single-node MVP scale.
    // This supersedes the previous MaximumParallelMessages(1) throttle: it is strictly more general
    // (covers HTTP and cross-message-type races) and drops the single-worker throughput ceiling.
    opts.OnException<ConcurrencyException>()
        .RetryWithCooldown(
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1));
});

builder.Services.AddAuthentication(ApiKeyAuthenticationDefaults.AuthenticationScheme)
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationDefaults.AuthenticationScheme, _ => { });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

builder.Services.AddWolverineHttp();
// Maps Marten's optimistic-concurrency failure on same-planet-stream appends to 409 (#39). The
// conflicting commit is issued by Wolverine's transactional middleware after the endpoint returns,
// so it must be handled here rather than inside the endpoint.
builder.Services.AddExceptionHandler<ConcurrencyConflictExceptionHandler>();
// One uniform error shape for every non-2xx (D12/#74): stamp each ProblemDetails with the request
// path as `Instance` and a `traceId` extension for correlation. Title/type are left to the framework
// defaults derived from the status code so the shape stays consistent across endpoints and the
// concurrency handler (which emits through this same IProblemDetailsService).
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance = context.HttpContext.Request.Path;
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    });
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITravelPlanner, LinearTravelPlanner>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(opts =>
{
    // Honor C# nullable reference types so the OpenAPI contract emits accurate
    // `required` + nullability metadata (consumed by the generated frontend client).
    opts.SupportNonNullableReferenceTypes();
    opts.SchemaFilter<RequiredNonNullableSchemaFilter>();

    opts.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Name = ApiKeyAuthenticationDefaults.HeaderName,
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
    });

    opts.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
    {
        { new OpenApiSecuritySchemeReference("ApiKey", doc), [] },
    });
});
builder.Services.Configure<WorldGenOptions>(builder.Configuration.GetSection("WorldGeneration"));
builder.Services.Configure<BalanceOptions>(builder.Configuration.GetSection("Balance"));
builder.Services.AddHostedService<WorldSeeder>();
builder.Services.AddHealthChecks().AddNpgSql(connectionString);

var app = builder.Build();

app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapWolverineEndpoints();

return await app.RunJasperFxCommands(args);

public partial class Program;
