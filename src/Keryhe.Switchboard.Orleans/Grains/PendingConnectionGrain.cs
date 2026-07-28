using Orleans;
using Orleans.Runtime;

namespace Keryhe.Switchboard.Orleans.Grains;

[GenerateSerializer]
public sealed class PendingConnectionGrainState
{
    [Id(0)]
    public PendingConnectionRecord? Record { get; set; }
}

public sealed class PendingConnectionGrain(
    [PersistentState("pending-connection", SwitchboardOrleansExtensions.StorageProviderName)] IPersistentState<PendingConnectionGrainState> state)
    : Grain, IPendingConnectionGrain
{

    public async Task AddAsync(PendingConnectionRecord record)
    {
        state.State.Record = record;
        await state.WriteStateAsync();
    }

    public async Task<PendingConnectionRecord?> TryConsumeAsync(DateTimeOffset now)
    {
        var record = state.State.Record;
        if (record is null)
        {
            return null;
        }

        state.State.Record = null;
        await state.WriteStateAsync();

        return record.ExpiresAt > now ? record : null;
    }
}
