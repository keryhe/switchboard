using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Server.ClientConnections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.ClientConnections;

/// <summary>
/// Unit-level coverage of <see cref="ClientConnectionForwarder"/> (plan decision D19, Phase 3 Slice
/// 5) — the owner-resolution decision tree and the single-hop marker header, isolated from real
/// network calls via a fake <see cref="HttpMessageHandler"/>. The full round-trip (a request
/// actually crossing to another node's real Kestrel host) is covered by the two-node end-to-end
/// tests instead; this file is about never forwarding when it shouldn't, and forwarding correctly
/// when it should.
/// </summary>
public class ClientConnectionForwarderTests
{
    private const string LocalNodeId = "node-local";
    private const string RemoteNodeId = "node-remote";
    private const string RemoteInternalUrl = "http://remote.invalid:5000";

    [Fact]
    public async Task TryForwardAsync_ReturnsFalse_WhenRequestAlreadyCarriesTheMarkerHeader()
    {
        // The ownership resolver would happily say "remote" here — the header check must short-
        // circuit before it is even consulted, which the throwing resolver below proves.
        var forwarder = BuildForwarder(
            new ThrowingOwnershipResolver(),
            new StubNodeAddressResolver(RemoteInternalUrl),
            new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)));

        var context = new DefaultHttpContext();
        context.Request.Headers[ClientConnectionForwarder.ForwardedHeaderName] = "1";

        var forwarded = await forwarder.TryForwardAsync(context, "conn-1", CancellationToken.None);

        Assert.False(forwarded);
    }

    [Fact]
    public async Task TryForwardAsync_ReturnsFalse_WhenNoOwnerIsKnown()
    {
        var forwarder = BuildForwarder(
            new StubOwnershipResolver(null),
            new StubNodeAddressResolver(RemoteInternalUrl),
            new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)));

        var forwarded = await forwarder.TryForwardAsync(new DefaultHttpContext(), "conn-1", CancellationToken.None);

        Assert.False(forwarded);
    }

    [Fact]
    public async Task TryForwardAsync_ReturnsFalse_WhenTheOwnerIsThisNode()
    {
        var forwarder = BuildForwarder(
            new StubOwnershipResolver(LocalNodeId),
            new StubNodeAddressResolver(RemoteInternalUrl),
            new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)));

        var forwarded = await forwarder.TryForwardAsync(new DefaultHttpContext(), "conn-1", CancellationToken.None);

        Assert.False(forwarded);
    }

    [Fact]
    public async Task TryForwardAsync_ForwardsToTheOwningNode_CarryingTheMarkerHeaderAndTheOriginalAuthorization()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("poll-bytes"),
            };
        });

        var forwarder = BuildForwarder(new StubOwnershipResolver(RemoteNodeId), new StubNodeAddressResolver(RemoteInternalUrl), handler);

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/chatHub";
        context.Request.QueryString = new QueryString("?id=tok-1&access_token=jwt-1");
        context.Request.Headers.Authorization = "Bearer jwt-1";
        context.Response.Body = new MemoryStream();

        var forwarded = await forwarder.TryForwardAsync(context, "conn-1", CancellationToken.None);

        Assert.True(forwarded);
        Assert.NotNull(captured);
        Assert.Equal("1", Assert.Single(captured!.Headers.GetValues(ClientConnectionForwarder.ForwardedHeaderName)));
        Assert.Equal("Bearer jwt-1", captured.Headers.Authorization?.ToString());
        Assert.Equal(new Uri($"{RemoteInternalUrl}/chatHub?id=tok-1&access_token=jwt-1"), captured.RequestUri);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        Assert.Equal("poll-bytes", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task TryForwardAsync_Returns502_AndDropsTheCachedAddress_WhenTheOwningNodeIsUnreachable()
    {
        var addressResolver = new StubNodeAddressResolver(RemoteInternalUrl);
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var forwarder = BuildForwarder(new StubOwnershipResolver(RemoteNodeId), addressResolver, handler);

        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        context.Request.Method = "DELETE";
        context.Request.Path = "/chatHub";

        var forwarded = await forwarder.TryForwardAsync(context, "conn-1", CancellationToken.None);

        Assert.True(forwarded);
        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Equal(1, addressResolver.CallCount);

        // The stale address must have been evicted from the node-local cache — a second attempt
        // re-resolves rather than retrying the same dead address forever.
        var secondContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        secondContext.Request.Method = "DELETE";
        secondContext.Request.Path = "/chatHub";
        await forwarder.TryForwardAsync(secondContext, "conn-1", CancellationToken.None);
        Assert.Equal(2, addressResolver.CallCount);
    }

    private static ClientConnectionForwarder BuildForwarder(
        ITransportOwnershipRegistry ownershipResolver,
        INodeAddressResolver nodeAddressResolver,
        HttpMessageHandler handler)
    {
        var options = Options.Create(new SwitchboardOptions
        {
            PublicUrl = "http://localhost",
            TokenSigningKey = "unit-test-signing-key-only-needs-length-32+",
            ServerSigningKey = "unit-test-signing-key-only-needs-length-32+",
            NodeId = LocalNodeId,
        });

        return new ClientConnectionForwarder(
            ownershipResolver,
            nodeAddressResolver,
            new SingleHandlerHttpClientFactory(handler),
            options,
            NullLogger<ClientConnectionForwarder>.Instance);
    }

    private sealed class StubOwnershipResolver(string? ownerNodeId) : ITransportOwnershipRegistry
    {
        public Task ClaimAsync(string connectionToken, string nodeId, CancellationToken ct) => Task.CompletedTask;
        public Task ReleaseAsync(string connectionToken, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetOwnerNodeIdAsync(string connectionToken, CancellationToken ct) => Task.FromResult(ownerNodeId);
    }

    private sealed class ThrowingOwnershipResolver : ITransportOwnershipRegistry
    {
        public Task ClaimAsync(string connectionToken, string nodeId, CancellationToken ct) =>
            throw new InvalidOperationException("Must not be consulted once the marker header is already present.");
        public Task ReleaseAsync(string connectionToken, CancellationToken ct) =>
            throw new InvalidOperationException("Must not be consulted once the marker header is already present.");
        public Task<string?> GetOwnerNodeIdAsync(string connectionToken, CancellationToken ct) =>
            throw new InvalidOperationException("Must not be consulted once the marker header is already present.");
    }

    private sealed class StubNodeAddressResolver(string? internalUrl) : INodeAddressResolver
    {
        public int CallCount { get; private set; }

        public Task<string?> GetInternalUrlAsync(string nodeId, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(internalUrl);
        }
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return respond(request);
        }
    }

    private sealed class SingleHandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
