using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Planets;

// #44: nothing guarantees that events appended to one Planet stream carry non-decreasing `at`.
// A completion scheduled for T can be delivered after a player command already committed at
// W > T (poll lag per ADR 0001 plus the #39 retry backoff), so RebaseRates runs with a backwards
// timestamp. These tests pin the invariant that such an inversion is inert rather than corrupting.
public sealed class PlanetEventOrderingTests
{
    private static Planet Homeworld(DateTimeOffset at)
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, at));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, at));
        planet.Apply(new BuildingPlaced(BuildingType.Refinery, at));
        planet.Apply(new BuildingPlaced(BuildingType.Generator, at));
        return planet;
    }

    // Two constructions started together, completing 30s apart.
    private static (Planet Planet, BuildingConstructionStarted Early, BuildingConstructionStarted Late) Staged(
        DateTimeOffset at)
    {
        var planet = Homeworld(at);
        var early = planet.StartConstruction(BuildingType.Drill, at, ingotCost: 300m, buildDurationSeconds: 30m);
        planet.Apply(early);
        var late = planet.StartConstruction(BuildingType.Generator, at, ingotCost: 600m, buildDurationSeconds: 60m);
        planet.Apply(late);
        return (planet, early, late);
    }

    private static void Complete(Planet planet, BuildingConstructionStarted started)
    {
        var events = planet.CompleteBuilding(started.SlotIndex, started.CompletesAt);
        planet.Apply((BuildingCompleted)events[0]);
    }

    private static Planet Created()
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
        return planet;
    }

    // WS2 (#44): the seeded-store injection still lands exactly at the colonization instant after
    // routing Apply(PlanetColonized) through the guarded Checkpoint — numerically identical to the
    // old raw `with` on the zero-rate/zero-value claim-time pool this event always lands on.
    [Fact]
    public void ColonizationSeedsStoresAtTheColonizationInstant()
    {
        var t = DateTimeOffset.UtcNow;
        var planet = Created();

        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, t));

        Assert.Equal(500m, planet.IronOre.CheckpointValue);
        Assert.Equal(100m, planet.IronIngot.CheckpointValue);
        Assert.Equal(t, planet.IronOre.CheckpointTime);
        Assert.Equal(t, planet.IronIngot.CheckpointTime);
    }

    // WS2 guard (#44): with the raw `with` gone, a colonization stamped BEHIND an already-advanced
    // pool checkpoint no longer rewinds CheckpointTime — the non-regressing Checkpoint freezes it.
    // The claim-time pool is zero-rate/zero-value in production so this can't happen there; the test
    // documents that the invariant is now enforced by the type, not by that convention.
    [Fact]
    public void ColonizingBehindAnAdvancedCheckpointDoesNotRegressIt()
    {
        var t = DateTimeOffset.UtcNow;
        var planet = Created();

        // Hypothetically advance the claim-time pools' checkpoint ahead of the colonization stamp.
        var head = t.AddSeconds(120);
        planet.IronOre = planet.IronOre with { CheckpointTime = head };
        planet.IronIngot = planet.IronIngot with { CheckpointTime = head };

        // A fleet claim seeds zero stores at the (earlier) arrival instant.
        planet.Apply(planet.Claim(Guid.NewGuid(), t));

        Assert.Equal(head, planet.IronOre.CheckpointTime);
        Assert.Equal(head, planet.IronIngot.CheckpointTime);
        Assert.Equal(0m, planet.IronOre.CheckpointValue);
        Assert.Equal(0m, planet.IronIngot.CheckpointValue);
    }

    [Fact]
    public void OutOfOrderCompletionNeverProducesNegativeElapsedOrBackwardsCheckpoint()
    {
        var now = DateTimeOffset.UtcNow;
        var (planet, early, late) = Staged(now);

        // The later-scheduled completion commits first; the earlier one arrives afterwards.
        Complete(planet, late);
        var checkpointAfterLate = planet.IronOre.CheckpointTime;
        Complete(planet, early);

        // The rewound completion must not drag either checkpoint backwards...
        Assert.Equal(checkpointAfterLate, planet.IronOre.CheckpointTime);
        Assert.Equal(checkpointAfterLate, planet.IronIngot.CheckpointTime);

        // ...nor leave a value that a later read re-accrues from a rewound baseline.
        Assert.True(planet.IronOre.CheckpointValue >= 0);
        Assert.True(planet.IronIngot.CheckpointValue >= 0);
    }

    private static (decimal Ore, decimal Ingot) BalancesAt(Planet planet, DateTimeOffset readAt) =>
        (planet.IronOre.GetCurrentValue(readAt), planet.IronIngot.GetCurrentValue(readAt));

    // Exact order-independence is NOT what this fix provides, and asserting it would be asserting
    // something no clamp can deliver: recovering the inverted interval requires retroactively
    // re-deriving the pool from the rewound timestamp under the post-completion rates (a
    // rewind-and-reapply model, explicitly out of scope for #44). What the fix does guarantee is
    // that an inversion is *inert and conservative* — the inverted window accrues at the
    // pre-completion rate, so a race can only ever under-credit, never corrupt or over-credit.
    [Fact]
    public void ReverseOrderCompletionsNeverCreditMoreThanInOrder()
    {
        var now = DateTimeOffset.UtcNow;

        var (inOrder, earlyA, lateA) = Staged(now);
        Complete(inOrder, earlyA);
        Complete(inOrder, lateA);

        var (reversed, earlyB, lateB) = Staged(now);
        Complete(reversed, lateB);
        Complete(reversed, earlyB);

        var readAt = now.AddSeconds(120);
        var ordered = BalancesAt(inOrder, readAt);
        var raced = BalancesAt(reversed, readAt);

        Assert.True(raced.Ore <= ordered.Ore, $"race over-credited ore: {raced.Ore} > {ordered.Ore}");
        Assert.True(raced.Ingot <= ordered.Ingot, $"race over-credited ingots: {raced.Ingot} > {ordered.Ingot}");
        Assert.True(raced.Ore >= 0);
        Assert.True(raced.Ingot >= 0);
    }

    // Pins the exact residual so a future change to the ordering model shows up here as a diff
    // rather than passing silently. The 30s inversion is deliberately far larger than production
    // can produce (~5s durable-message poll per ADR 0001 + ~1.9s of #39 retry backoff).
    [Fact]
    public void ReverseOrderShortfallIsTheInvertedWindowAtThePreCompletionRate()
    {
        var now = DateTimeOffset.UtcNow;

        var (inOrder, earlyA, lateA) = Staged(now);
        Complete(inOrder, earlyA);
        Complete(inOrder, lateA);

        var (reversed, earlyB, lateB) = Staged(now);
        Complete(reversed, lateB);
        Complete(reversed, earlyB);

        var readAt = now.AddSeconds(120);

        // The second drill lifts the net ore rate 5/s -> 15/s. In-order it applies from t=30;
        // reversed it only applies from t=60, so the 30s window accrues 10/s short.
        Assert.Equal(300m, BalancesAt(inOrder, readAt).Ore - BalancesAt(reversed, readAt).Ore);

        // Both orderings converge once the inverted window is behind them: rates are identical
        // from t=60 onward, so the gap is a constant offset, not a widening drift.
        var later = now.AddSeconds(600);
        Assert.Equal(300m, BalancesAt(inOrder, later).Ore - BalancesAt(reversed, later).Ore);
    }

    // Task 3 (#44): scheduled FLEET ARRIVALS are the one genuine inversion (ADR 0002). A dead-lettered
    // or host-down-replayed CargoDeliveredToStorage is stamped with its original ArrivesAt — which can
    // be far behind a destination checkpoint that intervening traffic has since advanced. Unlike the
    // ~7s command/completion race, that window is bounded only by outage/travel duration. Applying such
    // a far-past delivery must stay CONSERVATIVE: the checkpoint must not regress, the pool must stay in
    // [0, capacity], and the buffer may under-accept (cargo rides along) but must never be corrupted.
    [Fact]
    public void FarPastArrivalDeliveryStaysConservativeAndNonRegressing()
    {
        var now = DateTimeOffset.UtcNow;

        // The destination, and a control that receives the same intervening traffic but NOT the
        // far-past delivery — so the delta between them isolates exactly what the delivery contributed.
        var planet = Homeworld(now);
        var control = Homeworld(now);

        // Intervening in-order traffic advances the destination checkpoint 300s into the future. The
        // ingot leg lands the buffer near capacity (5000 cap; 3100 accrued + 1800 = 4900) on purpose.
        var head = now.AddSeconds(300);
        var forwardOre = 100m;
        var forwardIngot = 1800m;
        planet.Apply(planet.AcceptCargoDelivery(Guid.NewGuid(), forwardOre, forwardIngot, head));
        control.Apply(control.AcceptCargoDelivery(Guid.NewGuid(), forwardOre, forwardIngot, head));
        Assert.Equal(head, planet.IronOre.CheckpointTime);

        var oreBefore = planet.IronOre.GetCurrentValue(head);
        var ingotBefore = planet.IronIngot.GetCurrentValue(head);

        // A fleet that departed before `head` is replayed; its arrival is stamped with the original
        // ArrivesAt == now, far behind the current head (the match token is deliberately left exact).
        var delivered = planet.AcceptCargoDelivery(Guid.NewGuid(), ironOre: 1000m, ironIngot: 500m, at: now);
        Assert.Equal(now, delivered.At);

        // The ingot leg under-accepts: only ~100 of the 500 offered fits the near-full buffer, computed
        // against the floored (not rewound) headroom. Conservative, never a corrupting over-accept.
        Assert.True(delivered.IronIngot < 500m, $"ingot delivery did not under-accept: {delivered.IronIngot}");
        Assert.True(delivered.IronOre <= 1000m);
        planet.Apply(delivered);

        // Guarantee 1: the rewound delivery does not drag either checkpoint backwards.
        Assert.Equal(head, planet.IronOre.CheckpointTime);
        Assert.Equal(head, planet.IronIngot.CheckpointTime);

        // Guarantee 2: a positive delivery never REDUCES a pool (the floor absorbs the negative
        // elapsed — without it the buffer would silently drain) and never exceeds capacity.
        var oreAfter = planet.IronOre.GetCurrentValue(head);
        var ingotAfter = planet.IronIngot.GetCurrentValue(head);
        Assert.True(oreAfter >= oreBefore, $"delivery reduced ore: {oreAfter} < {oreBefore}");
        Assert.True(ingotAfter >= ingotBefore, $"delivery reduced ingots: {ingotAfter} < {ingotBefore}");
        Assert.InRange(oreAfter, 0m, planet.IronOre.StorageCapacity);
        Assert.InRange(ingotAfter, 0m, planet.IronIngot.StorageCapacity);

        // Guarantee 3: what the buffer actually gained is at most what the fleet carried — a far-past
        // stamp cannot fabricate resources.
        Assert.True(oreAfter - oreBefore <= 1000m);
        Assert.True(ingotAfter - ingotBefore <= 500m);

        // Mirror ReverseOrderShortfall's "constant offset, not widening drift": the delivery bumped
        // CheckpointValue, not Rate, so its contribution over the control is a fixed additive step —
        // identical whether read 120s or 600s past the head, never a widening drift.
        var deltaOre120 = BalancesAt(planet, head.AddSeconds(120)).Ore - BalancesAt(control, head.AddSeconds(120)).Ore;
        var deltaOre600 = BalancesAt(planet, head.AddSeconds(600)).Ore - BalancesAt(control, head.AddSeconds(600)).Ore;
        Assert.Equal(delivered.IronOre, deltaOre120);
        Assert.Equal(deltaOre120, deltaOre600);
    }
}
