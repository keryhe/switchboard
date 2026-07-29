using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace Keryhe.Switchboard.Protocol.Framing;

/// <summary>
/// The management REST API's own narrowly-chartered payload constructor (Phase 4 plan decision
/// D22) — a second, equally narrow type beside <see cref="ClientFrameWriter"/> rather than a
/// widening of it: invocation messages originating from the management REST API only. The service
/// is otherwise deliberately payload-agnostic on every client- and server-driven path; this is the
/// one place it constructs an <c>Invocation</c> from scratch, because a management send has no
/// existing hub-protocol bytes to forward.
/// </summary>
/// <remarks>
/// Management sends bypass app servers and hub code entirely — no <c>IHubContext</c>, no hub
/// filters, no <c>OnConnectedAsync</c>. The service injects the constructed message straight into
/// <c>IMessageRouter</c> fan-out, matching Azure SignalR's REST API semantics.
/// </remarks>
public static class ManagementInvocationWriter
{
    private static readonly IHubProtocol Json = new JsonHubProtocol();
    private static readonly IHubProtocol MessagePack = new MessagePackHubProtocol();

    /// <summary>
    /// Encodes <paramref name="target"/>/<paramref name="arguments"/> as an <c>Invocation</c> for
    /// every hub protocol a management send might need to reach (Phase 2 plan decision D7's
    /// <c>payloadsByProtocol</c> shape) — a management broadcast/group/user send has no idea in
    /// advance what protocols its recipients negotiated, exactly like app-server-originated fan-out.
    /// </summary>
    /// <remarks>
    /// <paramref name="arguments"/> arrives as raw <see cref="JsonElement"/>s straight from the
    /// request body. Handing those to <see cref="MessagePackHubProtocol"/> directly silently
    /// encodes every argument as an empty map — verified, no exception — because
    /// <c>JsonHubProtocol</c> happens to special-case <see cref="JsonElement"/> while
    /// <c>MessagePackHubProtocol</c> does not. <see cref="ToPrimitive"/> maps every element to a
    /// plain CLR primitive first, which both protocols encode correctly (finding 4).
    /// </remarks>
    public static IReadOnlyDictionary<string, byte[]> WriteInvocation(string target, IReadOnlyList<JsonElement>? arguments)
    {
        var args = arguments is null ? [] : arguments.Select(ToPrimitive).ToArray();

        return new Dictionary<string, byte[]>
        {
            ["json"] = Write(Json, target, args),
            ["messagepack"] = Write(MessagePack, target, args),
        };
    }

    private static byte[] Write(IHubProtocol protocol, string target, object?[] args)
    {
        var writer = new ArrayBufferWriter<byte>();
        protocol.WriteMessage(new InvocationMessage(target, args), writer);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Deliberately explicit <c>long</c>/<c>double</c> branching rather than
    /// <c>e.TryGetInt64(out var l) ? l : e.GetDouble()</c> — that expression's best-common-type is
    /// <c>double</c>, so every integer argument would silently arrive as a float under either
    /// protocol. Cast to <see cref="object"/> explicitly to keep the two branches distinct types.
    /// </summary>
    private static object? ToPrimitive(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? (object)integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Array => element.EnumerateArray().Select(ToPrimitive).ToArray(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToPrimitive(p.Value)),
        _ => null,
    };
}
