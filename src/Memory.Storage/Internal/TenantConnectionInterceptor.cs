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
        if (scope is null)
        {
            return "RESET app.organization_id; RESET app.project_id;";
        }

        return $"SET app.organization_id = '{scope.Organization.Value:D}'; "
             + $"SET app.project_id = '{scope.Project.Value:D}';";
    }
}
