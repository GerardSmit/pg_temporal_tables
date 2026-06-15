using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace PgTemporalTables.EntityFrameworkCore;

/// <summary>
/// Per-DbContext temporal state: the acting user id. (Time travel is
/// per-query via <see cref="PgTemporalQueryableExtensions.TemporalAsOf{TEntity}(IQueryable{TEntity}, DateTime)"/>.)
/// </summary>
public static class PgTemporalDbContextExtensions
{
    internal sealed class TemporalState
    {
        public string? UserId;
        public bool UserIdSet;
    }

    private static readonly ConditionalWeakTable<DbContext, TemporalState> States = new();

    internal static TemporalState GetState(DbContext context)
        => States.GetOrCreateValue(context);

    /// <summary>
    /// The identity stamped into <c>changed_by</c> for writes made through this
    /// context (sets the <c>temporal.user_id</c> GUC on every connection open).
    /// Overrides the provider configured via
    /// <see cref="PgTemporalDbContextOptionsBuilderExtensions.UseTemporalTables"/>.
    /// </summary>
    public static void SetTemporalUserId(this DbContext context, string? userId)
    {
        var state = GetState(context);
        state.UserId = userId;
        state.UserIdSet = true;
    }
}
