using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Registry;
using Keryhe.Switchboard.Server.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Observability;

/// <summary>
/// Phase 4 Slice 5 gate (plans/phase-4-management-and-observability.md §4, plan decision D26):
/// connection-lifecycle and routing-error log call sites must carry structured (named-placeholder)
/// fields, not interpolated strings — verified through a capturing <see cref="ILogger"/> that
/// inspects the raw <c>state</c> object's key/value pairs, not by substring-matching the rendered
/// message. A <c>LogWarning($"unknown connection {connectionId}")</c> would still "look right" in
/// console output but would carry no <c>ConnectionId</c> field for a backend to filter, group, or
/// alert on — exactly the defect this test is designed to catch that a string-content assertion
/// would miss.
/// </summary>
public class StructuredLoggingTests
{
    private const string HubName = "testHub-structured-logging";

    [Fact]
    public async Task RouteClientMessage_ToUnknownConnection_LogsStructuredConnectionIdField()
    {
        var capturingLogger = new CapturingLogger<DefaultMessageRouter>();
        var connectionRegistry = new InMemoryConnectionRegistry();
        var localTransportRegistry = new LocalTransportRegistry();
        var options = Microsoft.Extensions.Options.Options.Create(new SwitchboardOptions
        {
            PublicUrl = "https://switchboard.example",
            TokenSigningKey = "test-token-signing-key-0123456789",
            ServerSigningKey = "test-server-signing-key-0123456789",
        });

        var router = new DefaultMessageRouter(
            connectionRegistry, new Keryhe.Switchboard.Registry.InMemoryHubRegistry(), localTransportRegistry,
            new NoOpBackplane(), options, new SwitchboardMetrics(), new SwitchboardTracing(), capturingLogger);

        await router.RouteClientMessageAsync("connection-that-was-never-registered", new byte[] { 1, 2, 3 }, "json", CancellationToken.None);

        var entry = Assert.Single(capturingLogger.Entries, e => e.LogLevel == LogLevel.Warning);
        var connectionIdField = Assert.Single(entry.State, kv => kv.Key == "ConnectionId");
        Assert.Equal("connection-that-was-never-registered", connectionIdField.Value?.ToString());

        // The rendered message must still read naturally — structured logging is additive, not a
        // replacement for a human-readable line.
        Assert.Contains("unknown connection", entry.FormattedMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Captures every <c>Log&lt;TState&gt;</c> call's raw structured state (the
    /// <c>IReadOnlyList&lt;KeyValuePair&lt;string, object?&gt;&gt;</c> every named-placeholder
    /// message-template formatter produces) alongside the rendered message — deliberately not just
    /// the rendered string, since that's exactly what a naive interpolated-string log call would
    /// still pass.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel LogLevel, IReadOnlyList<KeyValuePair<string, object?>> State, string FormattedMessage)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var structuredState = state as IReadOnlyList<KeyValuePair<string, object?>>
                ?? throw new InvalidOperationException(
                    $"Log call did not use a structured message template: {formatter(state, exception)}");

            Entries.Add((logLevel, structuredState, formatter(state, exception)));
        }
    }
}
