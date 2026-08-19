using Alba;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;

namespace Voidforge.SoakTests;

// The scenario registry. Each SoakScenario is fully self-contained (DB + theme + body + intent + optional
// baseline); the fixtures, collections and test classes are thin wrappers that name one of these.
public static class SoakScenarios
{
    // The original two-user "economy + own-colony supply line + depletion + ingot-storage-full" scenario.
    public static SoakScenario TwoUserEconomy { get; } = new(
        Id: "two-user-economy-v1",
        DbName: "voidforge_soak_test",
        ApplyConfig: ApplyTwoUserEconomyTheme,
        Body: RunTwoUserEconomyAsync,
        Intent: ScenarioIntent.Default,
        BaselineFile: "soak-baseline.json");

    // A single-player scenario that isolates the INPUT-STARVATION halt: the seeded Drill mines a small
    // deposit that empties fast (ResourceDepleted), after which the Refinery drains the ore store and halts
    // InputStarved — while the ingot store keeps ample headroom so the ingot-storage-full path never fires.
    public static SoakScenario InputStarvation { get; } = new(
        Id: "input-starvation-v1",
        DbName: "voidforge_soak_starvation_test",
        ApplyConfig: ApplyInputStarvationTheme,
        Body: RunInputStarvationAsync,
        Intent: new ScenarioIntent(
            MinColoniesWon: 0,
            MinShipsProduced: 0,
            MinOreMined: 0m,
            CascadeWindowSeconds: 90,
            ExpectsTransport: false,
            ExpectsDepletion: false,
            ExpectedHalts: [HaltReason.InputStarved]),
        BaselineFile: null);

    // --- Two-user economy ---------------------------------------------------------------------------

    private static void ApplyTwoUserEconomyTheme()
    {
        // Rich-economy world: a finite ore deposit and an ingot store both sized so depletion and
        // ingot-storage-full can be reached within a few-minute window.
        SoakConfig.SetEnv("WorldGeneration__IronOrePool", "4000");
        SoakConfig.SetEnv("WorldGeneration__IronIngotStorageCapacity", "2500");
        SoakConfig.SetEnv("WorldGeneration__StartingIronOre", "2000");
        SoakConfig.SetEnv("WorldGeneration__StartingIronIngots", "800");
        SoakConfig.SetEnv("WorldGeneration__SolarSystemCount", "40");

        // Soak-scale construction durations and ship costs/speeds: fast enough to complete within the
        // window, slow enough that the real scheduler genuinely spreads completions across wall-clock.
        SoakConfig.SetEnv("Balance__Drill__BuildDurationSeconds", "20");
        SoakConfig.SetEnv("Balance__Refinery__BuildDurationSeconds", "20");
        SoakConfig.SetEnv("Balance__Generator__BuildDurationSeconds", "20");
        SoakConfig.SetEnv("Balance__Shipyard__BuildDurationSeconds", "20");
        SoakConfig.SetEnv("Balance__ColonyShip__BuildDurationSeconds", "15");
        SoakConfig.SetEnv("Balance__CargoVessel__BuildDurationSeconds", "15");
        SoakConfig.SetEnv("Balance__ColonyShip__IngotCost", "60");
        SoakConfig.SetEnv("Balance__CargoVessel__IngotCost", "60");
        SoakConfig.SetEnv("Balance__Ships__ColonyShip__SpeedPerSecond", "100");
        SoakConfig.SetEnv("Balance__Ships__CargoVessel__SpeedPerSecond", "100");

        // Economy is deliberately left unset: it binds from appsettings.json (10/s drill, 5/s refinery,
        // 1:2 ratio), which is exactly what the depletion math assumes.
        SoakConfig.PinLogLevels();
    }

    // A colonizes a second system and supplies it; B colonizes and recalls mid-transit. Each excludes its
    // own home system so the colonize legs reach out to another system. (Registration lives here, in the
    // body, because scenarios differ in player count — the driver only owns the reusable orchestration.)
    private static async Task RunTwoUserEconomyAsync(IAlbaHost host, SoakRecorder recorder, Deadline deadline)
    {
        var regA = await host.RegisterPlayer("SoakA_");
        var regB = await host.RegisterPlayer("SoakB_");
        var homeA = await host.GetPlanetById(regA, regA.HomeworldId);
        var homeB = await host.GetPlanetById(regB, regB.HomeworldId);

        var scriptA = ScenarioScript.RunIndustrialistAsync(host, recorder, regA, homeA.SolarSystemId, deadline);
        var scriptB = ScenarioScript.RunColonizerAsync(host, recorder, regB, homeB.SolarSystemId, deadline);
        await Task.WhenAll(scriptA, scriptB);
    }

    // --- Input starvation ---------------------------------------------------------------------------

    private static void ApplyInputStarvationTheme()
    {
        // Tiny world, single player. StartingIronOre gives the Refinery a finite ore buffer; once the
        // seeded Drill is demolished (below) there is NO inflow, so the buffer drains at the refinery's
        // 5/s and empties near t=30s -> InputStarved. Ore- and ingot-store caps are roomy so NEITHER
        // storage-full path fires, keeping this a clean input-starvation isolate. The deposit is untouched
        // (no drill), so there is no depletion — this isolates the input-starvation halt, nothing else.
        SoakConfig.SetEnv("WorldGeneration__SolarSystemCount", "10");
        SoakConfig.SetEnv("WorldGeneration__StartingIronOre", "150");
        SoakConfig.SetEnv("WorldGeneration__IronOreStorageCapacity", "5000");
        SoakConfig.SetEnv("WorldGeneration__IronIngotStorageCapacity", "5000");
        SoakConfig.SetEnv("WorldGeneration__StartingIronIngots", "0");

        // Short demolition so the seeded Drill's teardown COMPLETES in-window (default is 600s). The
        // completion (CompleteBuildingDemolition -> ScheduleAllChecksAsync) is what arms CheckInputStarved
        // on the now drill-free, draining planet; without it the check is never scheduled and the Refinery
        // never halts (economy rates otherwise left at appsettings defaults: drill 10/s, refinery 5/s ore).
        SoakConfig.SetEnv("Balance__DemolitionDurationSeconds", "5");
        SoakConfig.PinLogLevels();
    }

    // Register, then demolish the seeded Drill so ore inflow stops immediately: the Refinery drains the
    // starting ore buffer with no inflow, and the Demolish mutation (BuildingEndpoints.Demolish ->
    // ScheduleAllChecksAsync) arms the buffer-empty check on the now-draining planet, so the Refinery halts
    // InputStarved when the buffer hits 0. Deliberately DECOUPLED from depletion: a depletion-driven drill
    // halt does NOT re-arm the buffer check (CheckPoolDepletedHandler reschedules only depletion), and
    // racing the tiny-deposit/buffer timing is fragile — demolishing arms the check directly, at t~0.
    private static async Task RunInputStarvationAsync(IAlbaHost host, SoakRecorder recorder, Deadline deadline)
    {
        var reg = await host.RegisterPlayer("SoakStarve_");
        recorder.RecordEvent($"registered starvation player {reg.PlayerId}.");

        var home = await host.GetPlanetById(reg, reg.HomeworldId);
        var drillSlot = FindSlot(home, BuildingType.Drill);
        await host.DemolishBuilding(reg, drillSlot);
        recorder.RecordEvent(
            $"demolished seeded Drill (slot {drillSlot}); on teardown completion the drill-free Refinery drains "
            + "its ore buffer to 0 and halts InputStarved.");
        _ = deadline;
    }

    private static int FindSlot(PlanetResponse planet, BuildingType type)
    {
        for (var i = 0; i < planet.Buildings.Count; i++)
        {
            if (planet.Buildings[i].Type == type)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Seeded homeworld has no {type} to address.");
    }
}
