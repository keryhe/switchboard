namespace Keryhe.Switchboard.Protocol;

public interface IServerConnectionSelector
{
    ServerConnectionState? SelectConnection(string hubName);
}
