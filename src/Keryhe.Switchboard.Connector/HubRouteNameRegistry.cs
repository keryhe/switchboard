using System.Collections.Concurrent;

namespace Keryhe.Switchboard.Connector;

/// <summary>
/// Maps a hub CLR type to the route name it was mapped under (<c>MapHub&lt;ChatHub&gt;("/chatHub")</c>
/// -&gt; "chatHub"), discovered once at startup (plan decision D2) and consulted by
/// <see cref="SwitchboardHubLifetimeManager{THub}"/> to know which hub name to put on outbound envelopes.
/// </summary>
public sealed class HubRouteNameRegistry
{
    private readonly ConcurrentDictionary<Type, string> _names = new();

    public void Register(Type hubType, string hubName) => _names[hubType] = hubName;

    public string GetName(Type hubType) =>
        _names.TryGetValue(hubType, out var name)
            ? name
            : throw new InvalidOperationException(
                $"No route name registered for hub type '{hubType.Name}'. " +
                "This means AddSwitchboardConnector()'s hub discovery hasn't run yet, or the hub isn't mapped with MapHub<T>().");
}
