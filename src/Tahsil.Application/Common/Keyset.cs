using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Tahsil.Contracts;

namespace Tahsil.Application.Common;

/// <summary>
/// A list order for keyset pagination: a sort key, then the ID to break ties. Pages continue after the cursor's
/// position instead of skipping rows, so they stay correct and fast while rows are added.
/// </summary>
/// <param name="Sort">The name of the order, which cursors are bound to.</param>
/// <param name="Key">The sort key; never null.</param>
/// <param name="Id">The unique ID.</param>
/// <param name="Descending">Whether the list starts with the largest key, such as the newest first.</param>
public sealed record Keyset<T, TKey>(string Sort, Expression<Func<T, TKey>> Key, Expression<Func<T, Guid>> Id, bool Descending = false)
{
    private static readonly MethodInfo KeyCompareTo = typeof(TKey).GetMethod(nameof(IComparable<>.CompareTo), [typeof(TKey)])
        ?? throw new NotSupportedException($"{typeof(TKey)} has no CompareTo method.");

    private static readonly MethodInfo GuidCompareTo = typeof(Guid).GetMethod(nameof(Guid.CompareTo), [typeof(Guid)])!;

    private readonly Func<T, TKey> _key = Key.Compile();
    private readonly Func<T, Guid> _id = Id.Compile();

    /// <summary>
    /// Reads one page. Returns <see langword="null"/> when the cursor was not made for this order.
    /// </summary>
    public async Task<Page<T>?> ReadAsync(IQueryable<T> query, string? cursor, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, Paging.MaxLimit);

        if (cursor is not null)
        {
            if (!Cursor.TryDecode(cursor, Sort, out var position) || !Cursor.TryParseKey<TKey>(position.Key, out var key))
            {
                return null;
            }

            query = query.Where(After(key, position.Id));
        }

        var ordered = Descending
            ? query.OrderByDescending(Key).ThenByDescending(Id)
            : query.OrderBy(Key).ThenBy(Id);

        var items = await ordered.Take(limit + 1).ToListAsync(cancellationToken);
        if (items.Count <= limit)
        {
            return new Page<T>(items, null);
        }

        items.RemoveAt(limit);
        var last = items[^1];
        return new Page<T>(items, new Cursor(Sort, Cursor.FormatKey(_key(last)), _id(last)).Encode());
    }

    /// <summary><c>key &gt; k OR (key = k AND id &gt; i)</c>, with the comparisons reversed for a descending order.</summary>
    private Expression<Func<T, bool>> After(TKey key, Guid id)
    {
        var item = Key.Parameters[0];
        var itemKey = Key.Body;
        var itemId = new ParameterReplacer(Id.Parameters[0], item).Visit(Id.Body);

        // Captured values become query parameters, where constants would be inlined into the SQL.
        var keyValue = Expression.Property(Expression.Constant(new Captured<TKey>(key)), nameof(Captured<>.Value));
        var idValue = Expression.Property(Expression.Constant(new Captured<Guid>(id)), nameof(Captured<>.Value));

        var body = Expression.OrElse(
            Beyond(Expression.Call(itemKey, KeyCompareTo, keyValue)),
            Expression.AndAlso(
                Expression.Equal(itemKey, keyValue),
                Beyond(Expression.Call(itemId, GuidCompareTo, idValue))));

        return Expression.Lambda<Func<T, bool>>(body, item);
    }

    private BinaryExpression Beyond(Expression comparison) => Descending
        ? Expression.LessThan(comparison, Expression.Constant(0))
        : Expression.GreaterThan(comparison, Expression.Constant(0));

    private sealed record Captured<TValue>(TValue Value);

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
