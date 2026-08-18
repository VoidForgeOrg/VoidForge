using Microsoft.Extensions.Options;

namespace Voidforge.Api.Domain;

// Fail-fast validation for the config-bound "Economy" rate table. These values are installed into the
// process-global BuildingSpecs.Current (read during event replay) and drive the energy/scheduling
// math, so an invalid leaf must abort startup rather than silently corrupt the domain: a negative rate
// is nonsense, a draw factor outside [0, 1] is not a fraction, and a non-positive ShipyardParallelBuilds
// is used as a divisor in Planet.Energy (ceil(activeBuilds / ParallelBuilds)) and would divide by zero.
// Registered with ValidateOnStart (Program) so a bad section fails before any traffic is served.
public sealed class EconomyRatesValidator : IValidateOptions<EconomyRates>
{
    public ValidateOptionsResult Validate(string? name, EconomyRates options)
    {
        var failures = new List<string>();

        void NonNegative(string field, decimal value)
        {
            if (value < 0m)
            {
                failures.Add($"Economy:{field} must be >= 0 (was {value}).");
            }
        }

        void Fraction(string field, decimal value)
        {
            if (value is < 0m or > 1m)
            {
                failures.Add($"Economy:{field} must be within [0, 1] (was {value}).");
            }
        }

        NonNegative(nameof(options.DrillOreRatePerSecond), options.DrillOreRatePerSecond);
        NonNegative(nameof(options.RefineryOreConsumptionPerSecond), options.RefineryOreConsumptionPerSecond);
        NonNegative(nameof(options.RefineryIngotOutputFactor), options.RefineryIngotOutputFactor);
        NonNegative(nameof(options.GeneratorEnergyOutputMw), options.GeneratorEnergyOutputMw);
        NonNegative(nameof(options.DrillEnergyDrawMw), options.DrillEnergyDrawMw);
        NonNegative(nameof(options.RefineryEnergyDrawMw), options.RefineryEnergyDrawMw);
        NonNegative(nameof(options.ShipyardEnergyDrawMw), options.ShipyardEnergyDrawMw);

        Fraction(nameof(options.HaltedDrawFactor), options.HaltedDrawFactor);
        Fraction(nameof(options.ShipyardIdleDrawFactor), options.ShipyardIdleDrawFactor);

        if (options.ShipyardParallelBuilds <= 0)
        {
            failures.Add($"Economy:{nameof(options.ShipyardParallelBuilds)} must be > 0 (was {options.ShipyardParallelBuilds}).");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
