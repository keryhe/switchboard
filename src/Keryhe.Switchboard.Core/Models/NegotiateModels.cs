namespace Keryhe.Switchboard.Core.Models;

/// <summary>Step-1 redirect response. Carries only <see cref="Url"/> and <see cref="AccessToken"/> on the wire.</summary>
public sealed class RedirectResponse
{
    public required string Url { get; init; }
    public required string AccessToken { get; init; }
}

/// <summary>Step-2 connection response.</summary>
public sealed class NegotiateResponse
{
    public required string ConnectionId { get; init; }
    public required string ConnectionToken { get; init; }
    public required int NegotiateVersion { get; init; }
    public required IReadOnlyList<AvailableTransport> AvailableTransports { get; init; }
}

public sealed class AvailableTransport
{
    public required string Transport { get; init; }
    public required IReadOnlyList<string> TransferFormats { get; init; }
}
