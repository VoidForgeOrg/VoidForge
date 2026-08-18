using Microsoft.Extensions.Options;

namespace Voidforge.Api.Balance;

// Fail-fast validation for the config-bound "Balance" section. A non-positive duration schedules a
// completion at or before "now" — an immediate or past-dated construction/teardown — and a non-positive
// ship speed divides by zero when computing travel time (distance / speed, FleetEndpoints/Fleet).
// Registered with ValidateOnStart (Program) so a bad section fails before any traffic is served.
public sealed class BalanceOptionsValidator : IValidateOptions<BalanceOptions>
{
    public ValidateOptionsResult Validate(string? name, BalanceOptions options)
    {
        var failures = new List<string>();

        void Construction(string label, ConstructionBalance c)
        {
            if (c.BuildDurationSeconds <= 0m)
            {
                failures.Add($"Balance:{label}:BuildDurationSeconds must be > 0 (was {c.BuildDurationSeconds}).");
            }

            if (c.IngotCost < 0m)
            {
                failures.Add($"Balance:{label}:IngotCost must be >= 0 (was {c.IngotCost}).");
            }
        }

        Construction(nameof(options.Drill), options.Drill);
        Construction(nameof(options.Refinery), options.Refinery);
        Construction(nameof(options.Generator), options.Generator);
        Construction(nameof(options.Shipyard), options.Shipyard);
        Construction(nameof(options.ColonyShip), options.ColonyShip);
        Construction(nameof(options.CargoVessel), options.CargoVessel);

        void Ship(string label, ShipBalance s)
        {
            if (s.SpeedPerSecond <= 0m)
            {
                failures.Add($"Balance:Ships:{label}:SpeedPerSecond must be > 0 (was {s.SpeedPerSecond}).");
            }

            if (s.CargoCapacity < 0m)
            {
                failures.Add($"Balance:Ships:{label}:CargoCapacity must be >= 0 (was {s.CargoCapacity}).");
            }
        }

        Ship(nameof(options.Ships.ColonyShip), options.Ships.ColonyShip);
        Ship(nameof(options.Ships.CargoVessel), options.Ships.CargoVessel);

        if (options.DemolitionDurationSeconds <= 0m)
        {
            failures.Add($"Balance:{nameof(options.DemolitionDurationSeconds)} must be > 0 (was {options.DemolitionDurationSeconds}).");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
