using Xunit;

// Soak scenarios must NOT run concurrently within one process: the host installs a process-global economy
// rate table (BuildingSpecs.Current, "fixed for the process lifetime; differing rates require a separate
// process") and binds config via process-global environment variables set right before boot. Two scenario
// hosts booting concurrently would clobber each other's config and share one rate table. Parallelism is
// achieved across PROCESSES instead — one `dotnet test --filter` per scenario (scripts/soak-matrix.sh).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
