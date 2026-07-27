using System.Buffers;
using System.Text.Json;
using MessagePack;

namespace Keryhe.Switchboard.Protocol.Framing;

/// <summary>
/// Reads only the hub message <c>type</c> discriminator from a client frame — never a full
/// parse — so the service can classify a message (in particular, detect hub-level
/// <c>Ping</c>, type 6, so it can be absorbed rather than forwarded per D13) without
/// deserializing the message body. The service is otherwise deliberately payload-agnostic;
/// widening this into a general parser is a review failure, not a build failure.
/// </summary>
public static class HubMessageClassifier
{
    private const int PingType = 6;

    /// <summary><paramref name="frame"/> is a single frame as returned by
    /// <see cref="IHubProtocolFraming.TryReadFrame"/> — it includes its own framing.</summary>
    public static bool IsPing(string hubProtocol, ReadOnlySequence<byte> frame) =>
        TryReadType(hubProtocol, frame) == PingType;

    private static int? TryReadType(string hubProtocol, ReadOnlySequence<byte> frame) => hubProtocol switch
    {
        "json" => TryReadJsonType(frame),
        "messagepack" => TryReadMessagePackType(frame),
        _ => null,
    };

    private static int? TryReadJsonType(ReadOnlySequence<byte> frame)
    {
        // The frame includes its trailing record separator; JSON parsing needs it trimmed.
        if (frame.Length == 0)
        {
            return null;
        }

        var json = frame.Slice(0, frame.Length - 1);

        try
        {
            var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isType = reader.ValueTextEquals("type"u8);
                if (!reader.Read())
                {
                    return null;
                }

                if (isType)
                {
                    return reader.TokenType == JsonTokenType.Number ? reader.GetInt32() : null;
                }

                reader.Skip();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static int? TryReadMessagePackType(ReadOnlySequence<byte> frame)
    {
        if (!MessagePackFraming.TrySplitPrefixAndBody(frame, out var body))
        {
            return null;
        }

        try
        {
            var reader = new MessagePackReader(body);
            if (reader.End || reader.NextMessagePackType != MessagePackType.Array)
            {
                return null;
            }

            reader.ReadArrayHeader();
            return reader.End ? null : reader.ReadInt32();
        }
        catch (MessagePackSerializationException)
        {
            return null;
        }
    }
}
