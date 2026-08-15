namespace Voidforge.Api.Domain;

// A predicted instant at which a resource pool reaches its storage capacity (#69). Used to
// schedule CheckStorageFull messages (Task 2) at the fill time.
public sealed record StorageDeadline(ResourceType Resource, DateTimeOffset At);
