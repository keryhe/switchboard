using System.Collections.Concurrent;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// SSE/Long Polling node-affinity forward hop (plan decision D19, Phase 3 Slice 5). A
/// <c>connectionToken</c> not found in this node's own <see cref="ClientConnectionManager"/> is not
/// necessarily gone — the establishing request may have landed on a different node, since nothing
/// pins <c>POST</c>(send)/<c>GET</c>(poll)/<c>DELETE</c>(close) to the node that answered the
/// establishing <c>GET</c>. This resolves the owning node via <see cref="ITransportOwnershipRegistry"/>
/// (a grain lookup keyed by <c>connectionToken</c> itself, claimed at establish time — cluster-wide
/// and resolvable before the handshake that follows establishment even completes) and, when it names
/// a different node, forwards the raw HTTP request to that node's <see cref="SwitchboardOptions.InternalUrl"/>
/// over <see cref="INodeAddressResolver"/> — transparently to the client, which only ever talks to
/// <see cref="SwitchboardOptions.PublicUrl"/>.
///
/// Forwarding grants no authority (plan decision D19): the forwarded request carries the client's
/// original <c>access_token</c> unchanged, and the receiving node validates it exactly as it would a
/// direct request. A single-hop marker header (<see cref="ForwardedHeaderName"/>) is added to every
/// forwarded request and checked on the way in — a request that already carries it is never
/// forwarded again, so a stale owner cache on one node can never produce a forwarding loop between
/// two nodes.
/// </summary>
public sealed class ClientConnectionForwarder(
    ITransportOwnershipRegistry ownershipRegistry,
    INodeAddressResolver nodeAddressResolver,
    IHttpClientFactory httpClientFactory,
    IOptions<SwitchboardOptions> options,
    ILogger<ClientConnectionForwarder> logger)
{
    public const string HttpClientName = "switchboard-internal-forward";
    public const string ForwardedHeaderName = "X-Switchboard-Forwarded";

    /// <summary>Resolved once per owning node, then reused — a grain call per forwarded request
    /// would defeat the point of caching the (rare) cross-node case. Cleared for a node whose
    /// cached address turns out stale (that node restarted with a different <c>InternalUrl</c>, or
    /// left the cluster) so the next request re-resolves instead of retrying a dead address
    /// forever.</summary>
    private readonly ConcurrentDictionary<string, string> _internalUrlCache = new();

    /// <summary>
    /// Attempts to resolve <paramref name="connectionToken"/>'s owning node and, if it is not this
    /// node, forwards the request and writes the response. Returns <c>true</c> when the response has
    /// already been written (forwarded, successfully or not) — the caller must not write anything
    /// else. Returns <c>false</c> when there is nothing to forward (no known owner, the owner is
    /// this node itself, or the request already carries <see cref="ForwardedHeaderName"/>) — the
    /// caller falls back to its own local-miss handling (typically 404, or establishing a brand-new
    /// connection for Long Polling's <c>GET</c>).
    /// </summary>
    public async Task<bool> TryForwardAsync(HttpContext context, string connectionToken, CancellationToken ct)
    {
        if (context.Request.Headers.ContainsKey(ForwardedHeaderName))
        {
            // Already one hop from wherever this landed originally — never forward a second time,
            // even if the owner lookup below would otherwise say to. See the class doc comment.
            return false;
        }

        var ownerNodeId = await ownershipRegistry.GetOwnerNodeIdAsync(connectionToken, ct);
        if (ownerNodeId is null || ownerNodeId == options.Value.NodeId)
        {
            return false;
        }

        if (!_internalUrlCache.TryGetValue(ownerNodeId, out var internalUrl))
        {
            internalUrl = await nodeAddressResolver.GetInternalUrlAsync(ownerNodeId, ct);
            if (internalUrl is null)
            {
                logger.LogWarning("Cannot forward connectionToken {ConnectionToken} to node {NodeId}: no internal URL is published for it.", connectionToken, ownerNodeId);
                return false;
            }

            _internalUrlCache[ownerNodeId] = internalUrl;
        }

        if (!await ForwardOnceAsync(context, internalUrl, ct))
        {
            // The cached address may be stale (the owning node restarted with a different
            // InternalUrl, or left the cluster) — drop it so the next request re-resolves instead
            // of retrying the same dead address forever.
            _internalUrlCache.TryRemove(ownerNodeId, out _);
        }

        return true;
    }

    private async Task<bool> ForwardOnceAsync(HttpContext context, string internalUrl, CancellationToken ct)
    {
        var targetUri = new Uri(new Uri(internalUrl), context.Request.Path + context.Request.QueryString);

        using var forwardRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);
        forwardRequest.Headers.Add(ForwardedHeaderName, "1");

        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader))
        {
            forwardRequest.Headers.TryAddWithoutValidation("Authorization", authHeader);
        }

        if (HttpMethods.IsPost(context.Request.Method))
        {
            forwardRequest.Content = new StreamContent(context.Request.Body);
            if (!string.IsNullOrEmpty(context.Request.ContentType))
            {
                forwardRequest.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            }
        }

        var client = httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(forwardRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Forwarding {Method} {TargetUri} failed.", context.Request.Method, targetUri);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return false;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is not null)
            {
                context.Response.ContentType = response.Content.Headers.ContentType.ToString();
            }

            await response.Content.CopyToAsync(context.Response.Body, ct);
        }

        return true;
    }
}
