using System.IO.Pipelines;

namespace Keryhe.Switchboard.Connector.Dispatch;

/// <summary>
/// Framework-internal <c>DuplexPipe</c> isn't public (confirmed absent from every public
/// assembly during B0 recon), so the spike provides its own minimal implementation.
/// </summary>
public sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
{
    public PipeReader Input { get; } = input;
    public PipeWriter Output { get; } = output;
}
