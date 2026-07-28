using Orleans;
using Orleans.Runtime;

namespace Keryhe.Switchboard.Orleans.Grains;

[GenerateSerializer]
public sealed class ConnectionGrainState
{
    [Id(0)]
    public ConnectionRecord? Record { get; set; }
}

public sealed class ConnectionGrain(
    [PersistentState("connection", SwitchboardOrleansExtensions.StorageProviderName)] IPersistentState<ConnectionGrainState> state)
    : Grain, IConnectionGrain
{

    public async Task RegisterAsync(ConnectionRecord record)
    {
        state.State.Record = record;
        await state.WriteStateAsync();

        var connectionId = this.GetPrimaryKeyString();
        await GrainFactory.GetGrain<IHubGrain>(record.HubName).RegisterConnectionAsync(connectionId);

        if (record.UserId is not null)
        {
            await GrainFactory.GetGrain<IUserGrain>(UserGrainKey(record.HubName, record.UserId)).AddConnectionAsync(connectionId);
        }
    }

    public Task<ConnectionRecord?> GetAsync() => Task.FromResult(state.State.Record);

    public Task<ConnectionLocation?> GetOwnerNodeAsync() =>
        Task.FromResult(state.State.Record is null
            ? null
            : new ConnectionLocation { OwnerNodeId = state.State.Record.OwnerNodeId, HubName = state.State.Record.HubName });

    public async Task SetHubProtocolAsync(string hubProtocol)
    {
        if (state.State.Record is null)
        {
            return;
        }

        state.State.Record.HubProtocol = hubProtocol;
        await state.WriteStateAsync();
    }

    public async Task AddToGroupAsync(string groupName)
    {
        if (state.State.Record is null)
        {
            return;
        }

        if (state.State.Record.Groups.Add(groupName))
        {
            await state.WriteStateAsync();
        }

        await GrainFactory.GetGrain<IGroupGrain>(GroupGrainKey(state.State.Record.HubName, groupName))
            .AddAsync(this.GetPrimaryKeyString());
    }

    public async Task RemoveFromGroupAsync(string groupName)
    {
        if (state.State.Record is null)
        {
            return;
        }

        if (state.State.Record.Groups.Remove(groupName))
        {
            await state.WriteStateAsync();
        }

        await GrainFactory.GetGrain<IGroupGrain>(GroupGrainKey(state.State.Record.HubName, groupName))
            .RemoveAsync(this.GetPrimaryKeyString());
    }

    public async Task UnregisterAsync()
    {
        var record = state.State.Record;
        if (record is null)
        {
            return;
        }

        var connectionId = this.GetPrimaryKeyString();

        foreach (var groupName in record.Groups)
        {
            await GrainFactory.GetGrain<IGroupGrain>(GroupGrainKey(record.HubName, groupName)).RemoveAsync(connectionId);
        }

        if (record.UserId is not null)
        {
            await GrainFactory.GetGrain<IUserGrain>(UserGrainKey(record.HubName, record.UserId)).RemoveConnectionAsync(connectionId);
        }

        await GrainFactory.GetGrain<IHubGrain>(record.HubName).UnregisterConnectionAsync(connectionId);

        state.State.Record = null;
        await state.WriteStateAsync();
    }

    public async Task UnregisterWithoutHubCallbackAsync()
    {
        var record = state.State.Record;
        if (record is null)
        {
            return;
        }

        var connectionId = this.GetPrimaryKeyString();

        foreach (var groupName in record.Groups)
        {
            await GrainFactory.GetGrain<IGroupGrain>(GroupGrainKey(record.HubName, groupName)).RemoveAsync(connectionId);
        }

        if (record.UserId is not null)
        {
            await GrainFactory.GetGrain<IUserGrain>(UserGrainKey(record.HubName, record.UserId)).RemoveConnectionAsync(connectionId);
        }

        // Deliberately no IHubGrain.UnregisterConnectionAsync call here — see the interface doc
        // comment for why.
        state.State.Record = null;
        await state.WriteStateAsync();
    }

    private static string GroupGrainKey(string hubName, string groupName) => $"{hubName}::{groupName}";
    private static string UserGrainKey(string hubName, string userId) => $"{hubName}::{userId}";
}
