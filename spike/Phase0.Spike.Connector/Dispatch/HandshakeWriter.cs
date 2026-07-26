using System.IO.Pipelines;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace Phase0.Spike.Connector.Dispatch;

/// <summary>
/// HubConnectionHandler will not dispatch anything until a handshake completes. Since the real
/// handshake already happened between the client and the Switchboard service, the Connector
/// replays it locally against the synthetic connection. See docs/docs/04-design.md §11.
/// </summary>
public static class HandshakeWriter
{
    public static async Task WriteHandshakeRequestAsync(PipeWriter writer, string hubProtocol, int version = 1)
    {
        HandshakeProtocol.WriteRequestMessage(new HandshakeRequestMessage(hubProtocol, version), writer);
        await writer.FlushAsync();
    }
}
