namespace Keryhe.Switchboard.Core;

/// <summary>
/// Thrown by <see cref="INegotiationService.NegotiateAsync"/> when no app server has registered a
/// connection for the requested hub. Phase 1 fails fast (plan decision D5) rather than waiting/queueing —
/// the caller translates this into 503 Service Unavailable.
/// </summary>
public sealed class NoServerConnectionException(string hubName)
    : Exception($"No app servers registered for hub '{hubName}'.")
{
    public string HubName { get; } = hubName;
}
