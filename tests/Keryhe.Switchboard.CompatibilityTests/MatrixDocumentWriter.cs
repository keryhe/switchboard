using System.Text;

namespace Keryhe.Switchboard.CompatibilityTests;

/// <summary>
/// Plan decision D36: the compatibility matrix's deliverable is a document an adopter can read to
/// decide whether their app will work — "the tests pass" answers a different question. Exactly
/// three cell states exist: <see cref="MatrixStatus.Pass"/>, <see cref="MatrixStatus.NotApplicable"/>
/// (a cell the SDK or the service correctly refuses by design, e.g. SSE+MessagePack), and
/// <see cref="MatrixStatus.Untested"/> (a toolchain was unavailable in this environment — D34). A
/// failing cell is never written as a fourth state; the xunit assertion that produced it fails the
/// build first, so a red cell can never reach this document looking like anything other than red.
/// </summary>
public enum MatrixStatus
{
    Pass,
    NotApplicable,
    Untested,
}

public sealed record MatrixRow(string Sdk, string Transport, string Protocol, MatrixStatus Status, string? Note = null);

public sealed record KnownIncompatibility(string Title, string Description);

public static class MatrixDocumentWriter
{
    public static async Task WriteAsync(string path, IReadOnlyList<MatrixRow> rows, IReadOnlyList<KnownIncompatibility> knownIncompatibilities, DateTimeOffset generatedAt)
    {
        var content = Render(rows, knownIncompatibilities, generatedAt);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    public static string Render(IReadOnlyList<MatrixRow> rows, IReadOnlyList<KnownIncompatibility> knownIncompatibilities, DateTimeOffset generatedAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Compatibility Matrix");
        sb.AppendLine();
        sb.AppendLine("**Generated, not hand-maintained** — plan decision D36"
            + " ([plans/phase-5-compatibility-testing-and-benchmarking.md](../../plans/phase-5-compatibility-testing-and-benchmarking.md))."
            + " Produced by `tests/Keryhe.Switchboard.CompatibilityTests`'s matrix generation test, from a real run against a real"
            + " out-of-process `Keryhe.Switchboard.Server` + `SampleChatApp.Api` pair. Do not hand-edit this file — its content is"
            + " overwritten the next time that test runs.");
        sb.AppendLine();
        sb.AppendLine($"Last generated: {generatedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine("Every cell is one of exactly three states:");
        sb.AppendLine();
        sb.AppendLine("- **pass** — a real client of that SDK completed the full probe scenario (connect, receive a caller push, join a group, invoke a hub method, receive the resulting group message, disconnect cleanly) over that transport/protocol combination.");
        sb.AppendLine("- **not applicable** — the SDK or the service correctly refuses this combination by design (e.g. SSE is Text-only; a given SDK version ships no MessagePack hub protocol or no SSE transport at all).");
        sb.AppendLine("- **untested** — a required toolchain (e.g. Docker, for the Java row) was unavailable in the environment this document was generated in. Never silently omitted — see plan decision D34.");
        sb.AppendLine();
        sb.AppendLine("A failing cell fails the test run that generates this document; it never appears here as anything but one of the three states above.");
        sb.AppendLine();
        sb.AppendLine("## Matrix");
        sb.AppendLine();
        sb.AppendLine("| SDK | Transport | Protocol | Result | Note |");
        sb.AppendLine("|---|---|---|---|---|");

        foreach (var row in rows.OrderBy(r => r.Sdk, StringComparer.Ordinal)
                     .ThenBy(r => r.Transport, StringComparer.Ordinal)
                     .ThenBy(r => r.Protocol, StringComparer.Ordinal))
        {
            var symbol = row.Status switch
            {
                MatrixStatus.Pass => "pass",
                MatrixStatus.NotApplicable => "not applicable",
                MatrixStatus.Untested => "untested",
                _ => throw new ArgumentOutOfRangeException(nameof(rows)),
            };

            sb.AppendLine($"| {row.Sdk} | {row.Transport} | {row.Protocol} | {symbol} | {row.Note ?? ""} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Known Incompatibilities");
        sb.AppendLine();
        sb.AppendLine("Documented behavioral differences from an unmodified SignalR deployment, each pinned by an executable"
            + " assertion (plan decision D31) so a silent behavior change fails a test rather than only going stale in prose.");
        sb.AppendLine();

        foreach (var item in knownIncompatibilities)
        {
            sb.AppendLine($"- **{item.Title}.** {item.Description}");
        }

        return sb.ToString();
    }
}
