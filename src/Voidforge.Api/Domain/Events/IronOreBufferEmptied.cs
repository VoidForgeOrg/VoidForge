namespace Voidforge.Api.Domain.Events;

// The stored IronOre buffer drained to empty while a Refinery ran at REDUCED throughput — positive
// but insufficient drill inflow (#70). EvaluateInputStarvation emits no halt in this case (inflow > 0),
// so without this event no composition change would fire and rates would stay frozen at full refinery
// demand — over-producing ingots at factor*demand instead of the sustainable factor*inflow. This is a
// composition-neutral marker: Apply just re-derives rates against the now-empty buffer, so
// EffectiveOreConsumption clamps consumption to the current inflow.
public sealed record IronOreBufferEmptied(DateTimeOffset At);
