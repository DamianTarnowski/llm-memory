using System.Data.Common;
using Memory.Domain;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Memory.Storage.Internal;

internal sealed class TenantConnectionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ApplyScopeAsync(connection, tenant.Current, cancellationToken).GetAwaiter().GetResult();
        return Task.CompletedTask;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyScope(connection, tenant.Current);
    }

    private static void ApplyScope(DbConnection connection, TenantScope? scope)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = BuildSetSql(scope);
        cmd.ExecuteNonQuery();
    }

    private static async Task ApplyScopeAsync(DbConnection connection, TenantScope? scope, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = BuildSetSql(scope);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string BuildSetSql(TenantScope? scope)
    {
        // Use zero-UUID as a sentinel "no scope" — never matches a real org/project,
        // so RLS hides every tenant-scoped row. Avoids RESET-on-undefined-GUC errors
        // and keeps the SELECTs predictable when no scope is active.
        var orgId = scope?.Organization.Value.ToString("D") ?? "00000000-0000-0000-0000-000000000000";
        var projectId = scope?.Project.Value.ToString("D") ?? "00000000-0000-0000-0000-000000000000";
        return $"SET app.organization_id = '{orgId}'; SET app.project_id = '{projectId}';";
    }
}
