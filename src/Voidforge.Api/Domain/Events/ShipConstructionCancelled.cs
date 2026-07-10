namespace Voidforge.Api.Domain.Events;

public sealed record ShipConstructionCancelled(Guid BuildId, DateTimeOffset CancelledAt);
