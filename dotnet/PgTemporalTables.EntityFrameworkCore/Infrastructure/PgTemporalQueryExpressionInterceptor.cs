using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PgTemporalTables.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Rewrites <see cref="PgTemporal.ModDate{TEntity}"/> /
/// <see cref="PgTemporal.ModUser{TEntity}"/> calls in the LINQ tree to
/// <c>EF.Property</c> accesses on the ValidFrom / ChangedBy shadow properties
/// before query compilation. Pure expression-level rewrite — no provider
/// services involved.
/// </summary>
public sealed class PgTemporalQueryExpressionInterceptor : IQueryExpressionInterceptor
{
    /// <summary>The singleton instance of this interceptor.</summary>
    public static readonly PgTemporalQueryExpressionInterceptor Instance = new();

    private PgTemporalQueryExpressionInterceptor()
    {
    }

    /// <inheritdoc/>
    public Expression QueryCompilationStarting(Expression queryExpression,
        QueryExpressionEventData eventData)
        => Rewriter.Instance.Visit(queryExpression);

    private sealed class Rewriter : ExpressionVisitor
    {
        public static readonly Rewriter Instance = new();

        private static readonly MethodInfo EfPropertyMethod =
            typeof(EF).GetMethod(nameof(EF.Property))!;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(PgTemporal) &&
                node.Method.IsGenericMethod)
            {
                switch (node.Method.Name)
                {
                    case nameof(PgTemporal.ModDate):
                        return Expression.Call(
                            EfPropertyMethod.MakeGenericMethod(typeof(DateTime)),
                            Visit(node.Arguments[0]),
                            Expression.Constant(PgTemporalTableBuilderExtensions.ValidFromProperty));

                    case nameof(PgTemporal.ModUser):
                        return Expression.Call(
                            EfPropertyMethod.MakeGenericMethod(typeof(string)),
                            Visit(node.Arguments[0]),
                            Expression.Constant(PgTemporalTableBuilderExtensions.ChangedByProperty));
                }
            }

            return base.VisitMethodCall(node);
        }
    }
}
