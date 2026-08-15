namespace Voidforge.Api.Endpoints;

// Optional cargo to load at assembly time (spec §2.3/§4, #50). Null or both-zero means
// "no cargo requested" — the assembly endpoint skips cargo validation entirely in that case.
public sealed record CargoRequest(decimal IronOre, decimal IronIngot);
