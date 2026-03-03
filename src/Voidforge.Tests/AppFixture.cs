using Alba;
using Xunit;

namespace Voidforge.Tests;

public sealed class AppFixture : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connStr = Environment.GetEnvironmentVariable("ConnectionStrings__Marten")
            ?? "Host=localhost;Port=5432;Database=voidforge_test;Username=postgres;Password=voidforge_dev";

        // ASP.NET Core maps env vars with __ to : in the config hierarchy.
        // Set before AlbaHost.For<Program>() so the host picks it up automatically.
        // Using the env var path avoids AlbaHost.For's WithWebHostBuilder overload,
        // which triggers a service provider disposal race with RunJasperFxCommands in .NET 9.
        Environment.SetEnvironmentVariable("ConnectionStrings__Marten", connStr);

        Host = await AlbaHost.For<Program>();
    }

    public Task DisposeAsync() => Host?.DisposeAsync().AsTask() ?? Task.CompletedTask;
}
