using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Keryhe.Switchboard.Protocol.Framing;

namespace Keryhe.Switchboard.Benchmarks;

/// <summary>
/// D33 hot-path suite 5: <see cref="ManagementInvocationWriter.WriteInvocation"/>'s
/// <c>JsonElement</c> → CLR-primitive argument mapping (Phase 4 plan decision D22) — every
/// management-API send (broadcast/group/user, REST-originated, no app server involved) pays this
/// cost once per call, encoding into both hub protocols.
/// </summary>
[MemoryDiagnoser]
public class ManagementInvocationWriterBenchmarks
{
    private JsonElement[] _mixedArguments = null!;

    [GlobalSetup]
    public void Setup()
    {
        using var document = JsonDocument.Parse(
            """["hello", 42, 3.14, true, false, null, {"nested":{"a":1,"b":[1,2,3]}}, [1,2,3,4,5]]""");

        // JsonDocument is disposed at the end of this method, but JsonElement.Clone() detaches
        // each element from the underlying document so it stays valid for the lifetime of the
        // benchmark run — WriteInvocation only ever receives already-parsed, already-owned
        // elements from the real ASP.NET Core model binder in production, never a live document.
        _mixedArguments = document.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    [Benchmark]
    public IReadOnlyDictionary<string, byte[]> WriteInvocation_MixedArgumentTypes() =>
        ManagementInvocationWriter.WriteInvocation("Echo", _mixedArguments);

    [Benchmark]
    public IReadOnlyDictionary<string, byte[]> WriteInvocation_NoArguments() =>
        ManagementInvocationWriter.WriteInvocation("Ping", arguments: null);
}
