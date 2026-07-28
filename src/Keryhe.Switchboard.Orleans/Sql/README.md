# Orleans ADO.NET schema (vendored)

These scripts are vendored verbatim from the [dotnet/orleans](https://github.com/dotnet/orleans)
repository (`src/AdoNet/Shared/`, `src/AdoNet/Orleans.Clustering.AdoNet/`, and
`src/AdoNet/Orleans.Persistence.AdoNet/`, `main` branch, pulled for Orleans 10.2.2 to match this
repo's pinned package version). They are **not** shipped inside
`Microsoft.Orleans.Clustering.AdoNet` / `Microsoft.Orleans.Persistence.AdoNet` themselves (verified —
those packages contain `lib/` and a `README.md` only, nothing else), so vendoring and documenting
them here is the deliverable, not writing them from scratch. **Switchboard does not create these
tables itself** — the operator applies the scripts for their database engine before the first node
with `UseOrleansCluster: true` and `OrleansAdoNetConnectionString` set boots.

Do not hand-edit these files. If Orleans is upgraded and its schema has changed, re-pull the scripts
for the new version from the same repository paths rather than patching these in place.

## Which script, in which order

Pick one directory matching `OrleansAdoNetInvariant` and run all three scripts in that directory, in
this order, against an empty database created for this purpose:

1. `*-Main.sql` — the `OrleansQuery` table (a key/text lookup Orleans' ADO.NET providers use to hold
   every other query, populated by both scripts below) and other shared prerequisites. Vendored from
   `src/AdoNet/Shared/`, not from either the clustering or the persistence package directory — easy
   to miss, and both of the other scripts fail outright (`relation "orleansquery" does not exist`,
   verified) without it having run first.
2. `*-Clustering.sql` — silo membership (`OrleansMembershipTable`, `OrleansMembershipVersionTable`)
   and the stored procedures/functions `UseAdoNetClustering` calls.
3. `*-Persistence.sql` — grain state storage (`OrleansStorage`) and the stored
   procedures/functions `AddAdoNetGrainStorage` calls. This is what backs every `[PersistentState]`
   grain in `Keryhe.Switchboard.Orleans.Grains` — connection, hub, group, user, pending-connection,
   node-registry, connection-token-owner.

| Directory | `OrleansAdoNetInvariant` | Driver package | Wired in `Keryhe.Switchboard.Server` out of the box? |
|---|---|---|---|
| `PostgreSQL/` | `Npgsql` | `Npgsql` | Yes — the reference driver this host ships configured; see below |
| `SqlServer/` | `Microsoft.Data.SqlClient` | `Microsoft.Data.SqlClient` | No — add the package + one DI registration; see below |
| `MySQL/` | `MySql.Data.MySqlClient` or `MySqlConnector` | `MySqlConnector` (or `MySql.Data`) | No — add the package + one DI registration; see below |

None of this is special-cased in `Keryhe.Switchboard.Orleans` itself — that project has no database
driver dependency and no default engine. Which provider is wired up is entirely a
`Keryhe.Switchboard.Server` (or any other host composing `AddSwitchboardOrleans`) concern; Postgres
just happens to be the one this repo's own host configures by default. See "Provider factory
registration" below for how to swap it, or add another alongside it.

Both tables are shared by every node in the cluster — one database, not one per node (ADR-002's
single-database deployment model).

## Known upstream gap: `CleanupDefunctSiloEntriesKey` (clustering only)

The base `*-Clustering.sql` in every vendor directory — at every Orleans version checked, including
the exact `v10.2.2` tag this package is pinned to and current `main` — never defines a
`CleanupDefunctSiloEntriesKey` row in `OrleansQuery`, but Orleans' `DbStoredQueries` constructor
validates unconditionally (via reflection over every internal property) that **all** expected query
keys exist, and throws `Not all required queries found. Missing are: CleanupDefunctSiloEntriesKey` on
first membership access otherwise — verified: every ADO.NET-clustered silo fails at that point,
regardless of database engine. This is a real, still-open upstream bug
([dotnet/orleans#8676](https://github.com/dotnet/orleans/issues/8676)), not a vendoring mistake here.

The fix is vendored too, in `<Engine>/Migrations/`, one directory level down from the base scripts —
run it immediately after `*-Clustering.sql` (before `*-Persistence.sql`), even on a brand-new
database:

- `PostgreSQL/Migrations/PostgreSQL-Clustering-3.7.0.sql`
- `SqlServer/Migrations/SQLServer-Clustering-3.7.0.sql`
- `MySQL/Migrations/MySQL-Clustering-3.7.0.sql`

`PostgreSQL/Migrations/PostgreSQL-Clustering-3.6.0.sql` is also vendored for completeness but is
**not** needed on a fresh database — it retypes two timestamp columns and recreates two functions for
deployments upgrading from a pre-3.6.0 schema; a schema created from this repo's `*-Main.sql` +
`*-Clustering.sql` already has the 3.6.0 shape.

`PostgresContainerFixture` (`tests/Keryhe.Switchboard.UnitTests/TestSupport/`) applies exactly this
order — `*-Main.sql` → `*-Clustering.sql` → `*-Clustering-3.7.0.sql` → `*-Persistence.sql` — against a
throwaway `postgres:16-alpine` container for `OrleansAdoNetTwoNodeEndToEndTests`, the Phase 3 Slice 6
gate test.

## Provider factory registration (configured through DI)

.NET does not auto-register ADO.NET provider factories the way classic .NET Framework's
`machine.config` did. Orleans' `UseAdoNetClustering`/`AddAdoNetGrainStorage` resolve a provider by
`OrleansAdoNetInvariant` through the process-global `System.Data.Common.DbProviderFactories` registry,
so *something* has to call `DbProviderFactories.RegisterFactory(invariant, factory)` before the silo
starts.

`Keryhe.Switchboard.Orleans` deliberately has no opinion on which database engine that is — it takes
no ADO.NET driver package dependency and defaults to nothing. Instead, the **host application**
registers a keyed `DbProviderFactory` singleton in DI (`Microsoft.Extensions.DependencyInjection`'s
keyed-service support), keyed by the invariant name, and
`SwitchboardOrleansExtensions.RegisterConfiguredAdoNetProviderFactory(IServiceProvider, string invariant)`
bridges that DI registration into `DbProviderFactories` — called once from
`Keryhe.Switchboard.Server.Program.BuildApp`, right after `builder.Build()` and before the app runs
(Orleans doesn't actually touch the database until its own hosted service starts, so there's a safe
window between the two).

### The default: Postgres, already wired in `Keryhe.Switchboard.Server`

`Keryhe.Switchboard.Server.csproj` references `Npgsql`, and `Program.cs` registers it:

```csharp
builder.Services.AddKeyedSingleton<System.Data.Common.DbProviderFactory>("Npgsql", Npgsql.NpgsqlFactory.Instance);
```

Set `Switchboard:OrleansAdoNetInvariant` to `Npgsql` (or omit it and rely on nothing else being
configured — but see "no silent default" below, it's still required) and
`Switchboard:OrleansAdoNetConnectionString` to a Postgres connection string, and this is all that's
needed — no code changes.

### Configuring a different provider (SQL Server, MySQL, or anything else)

1. Add that engine's driver package to `Keryhe.Switchboard.Server.csproj` (or whichever project hosts
   your composition root), e.g. `Microsoft.Data.SqlClient` or `MySqlConnector`.
2. Register a keyed `DbProviderFactory` for it in `Program.cs`, using the same invariant name you'll
   put in config — e.g. for SQL Server:
   ```csharp
   builder.Services.AddKeyedSingleton<System.Data.Common.DbProviderFactory>(
       "Microsoft.Data.SqlClient", Microsoft.Data.SqlClient.SqlClientFactory.Instance);
   ```
   or for MySQL (via `MySqlConnector`):
   ```csharp
   builder.Services.AddKeyedSingleton<System.Data.Common.DbProviderFactory>(
       "MySqlConnector", MySqlConnector.MySqlConnectorFactory.Instance);
   ```
   Multiple keyed registrations can coexist (e.g. Npgsql stays registered even if you add SQL Server
   too) — only the one matching the configured invariant is ever resolved.
3. Set `Switchboard:OrleansAdoNetInvariant` to that same invariant string and
   `Switchboard:OrleansAdoNetConnectionString` to a matching connection string.
4. Run that engine's vendored scripts from the matching directory above, in the documented order
   (including the `CleanupDefunctSiloEntriesKey` migration).

If you'd rather not touch `Program.cs` at all — e.g. building a different host entirely — register
the provider with `System.Data.Common.DbProviderFactories.RegisterFactory(invariant, factory)`
yourself, anywhere that runs before the silo starts; the DI-keyed path is a convenience
`RegisterConfiguredAdoNetProviderFactory` layers on top of it, not a requirement — it checks
`DbProviderFactories.TryGetFactory` first and no-ops if something is already registered for the
invariant.

### No silent default

`Keryhe.Switchboard.Server`'s options validation requires `OrleansAdoNetInvariant` whenever
`OrleansAdoNetConnectionString` is set — there is no fallback engine. Getting this wrong (e.g. a typo
in the invariant, or forgetting to register a keyed factory for it) fails at Orleans' own connection
attempt with a clear provider-not-found error, not a silent fall-through to some other database.

## Sizing note

`SwitchboardOptions.ServerConnectionsPerHub` (per app server, default 5) times the number of app
server instances is the rough ceiling on live `IConnectionGrain`/`IHubGrain` activity per hub — size
the database's connection pool and the Orleans ADO.NET providers' own pooling with that in mind for
large deployments, not against node count alone (Phase 3 targets single-digit node counts; the
client-connection count, not the node count, is what actually drives row volume in `OrleansStorage`).
