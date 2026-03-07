using Voidforge.Api.Domain.Events;

namespace Voidforge.Api.Domain;

public sealed class Player
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset RegisteredAt { get; set; }

    public void Apply(PlayerRegistered @event)
    {
        Name = @event.Name;
        RegisteredAt = @event.RegisteredAt;
    }
}
