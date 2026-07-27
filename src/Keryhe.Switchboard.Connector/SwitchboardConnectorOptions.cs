namespace Keryhe.Switchboard.Connector;

public sealed class SwitchboardConnectorOptions
{
    public required string ServiceUrl { get; set; }
    public required string ServerAccessToken { get; set; }
    public int ServerConnectionsPerHub { get; set; } = 5;
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxReconnectAttempts { get; set; } = 0;
}
