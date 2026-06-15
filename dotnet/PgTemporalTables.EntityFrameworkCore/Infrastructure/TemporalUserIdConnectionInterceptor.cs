using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace PgTemporalTables.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Sets the <c>temporal.user_id</c> GUC on every connection open, so all
/// writes made through the context are attributed. The value comes from
/// <see cref="PgTemporalDbContextExtensions.SetTemporalUserId"/> (per-context
/// override) or the provider configured in UseTemporalTables(). Npgsql resets
/// session state when pooled physical connections are reused, which is exactly
/// why this runs per open.
/// </summary>
public sealed class TemporalUserIdConnectionInterceptor : DbConnectionInterceptor
{
    /// <summary>The singleton instance of this interceptor.</summary>
    public static readonly TemporalUserIdConnectionInterceptor Instance = new();

    private TemporalUserIdConnectionInterceptor()
    {
    }

    /// <inheritdoc/>
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (ResolveUserId(eventData) is { } userId)
            Execute(connection, SetUserIdSql(userId));
    }

    /// <inheritdoc/>
    public override async Task ConnectionOpenedAsync(DbConnection connection,
        ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (ResolveUserId(eventData) is { } userId)
            await ExecuteAsync(connection, SetUserIdSql(userId), cancellationToken).ConfigureAwait(false);
    }

    private static string? ResolveUserId(ConnectionEndEventData eventData)
    {
        var context = eventData.Context;
        if (context is null)
            return null;

        var state = PgTemporalDbContextExtensions.GetState(context);
        if (state.UserIdSet)
            return state.UserId;

        var extension = context.GetService<IDbContextOptions>()
            .FindExtension<PgTemporalOptionsExtension>();
        return extension?.UserIdProvider?.Invoke(context);
    }

    private static string SetUserIdSql(string userId)
        => $"SET temporal.user_id = '{userId.Replace("'", "''")}'";

    private static void Execute(DbConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql,
        CancellationToken cancellationToken)
    {
        var cmd = connection.CreateCommand();
        await using (cmd.ConfigureAwait(false))
        {
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
