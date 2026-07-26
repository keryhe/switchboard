namespace Phase0.Spike.Host;

/// <summary>
/// Dev-only signing material shared between the host's JWT-bearer auth setup and the test
/// project (which mints tokens to call the host's negotiate endpoints). Spike scaffolding only —
/// never carried forward to Phase 1.
/// </summary>
public static class TestConstants
{
    public const string SigningKey = "phase0-spike-dev-signing-key-not-for-production-use-01234567890";
    public const string Issuer = "phase0-spike";
    public const string Audience = "phase0-spike-app";
}
