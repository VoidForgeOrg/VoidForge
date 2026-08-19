namespace Voidforge.SoakTests;

// A self-contained soak scenario. Adding a new scenario is exactly one SoakScenario instance (see
// SoakScenarios) + a one-line fixture subclass + a 3-line test class — the evaluation half (all three
// tiers, drain, snapshot, the HTTP vocabulary) is scenario-agnostic and reused as-is.
//
// - Id:           stable identity, stamped into the Tier-2 baseline JSON + emitter marker.
// - DbName:       the scenario's OWN database on the shared Postgres server (must contain "test" for the
//                 drop-schema safety guard). Per-scenario DBs let scenarios run as concurrent PROCESSES
//                 (see scripts/soak-matrix.sh); in-process they stay serial because the host installs a
//                 process-global economy table (BuildingSpecs) and config via process-global env vars.
// - ApplyConfig:  the world/balance THEME as env-var overrides, applied before the host boots. Must NOT
//                 set the connection string — the fixture owns that (it is derived from DbName).
// - Body:         the driver body (registration + scripts).
// - Intent:       the Tier-3 thresholds AND which outcomes apply (ExpectsTransport / ExpectsDepletion /
//                 ExpectedHalts) — so a differently-shaped scenario asserts only its own story.
// - BaselineFile: the Tier-2 blessed baseline filename under baselines/, or null to SKIP Tier 2 entirely.
public sealed record SoakScenario(
    string Id,
    string DbName,
    Action ApplyConfig,
    SoakScenarioBody Body,
    ScenarioIntent Intent,
    string? BaselineFile);
