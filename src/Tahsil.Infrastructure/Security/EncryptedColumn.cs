using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Infrastructure.Security;

/// <summary>A <c>bytea</c> column of encrypted values in a table of the Tahsil schema keyed by a <c>uuid</c> <c>id</c>.</summary>
internal sealed partial class EncryptedColumn : IEncryptedStore
{
    private readonly string _purpose;
    private readonly bool _boundToRow;
    private readonly string _selectSql;
    private readonly string _updateSql;

    /// <param name="table">The table, without the schema.</param>
    /// <param name="column">The encrypted column.</param>
    /// <param name="purpose">The purpose the values were encrypted for.</param>
    /// <param name="boundToRow">Whether the row ID is the associated data of each value.</param>
    public EncryptedColumn(string table, string column, string purpose, bool boundToRow = true)
    {
        // Identifiers cannot be SQL parameters; these come from code, and are checked all the same.
        if (!Identifier().IsMatch(table) || !Identifier().IsMatch(column))
        {
            throw new ArgumentException($"'{table}.{column}' is not a plain table and column name.");
        }

        Name = $"{table}.{column}";
        _purpose = purpose;
        _boundToRow = boundToRow;

        var qualified = $"{TahsilDbContext.Schema}.{table}";
        _selectSql = $"""
            SELECT id AS "Id", {column} AS "Value" FROM {qualified}
            WHERE {column} IS NOT NULL AND (length({column}) < 2 OR get_byte({column}, 0) <> 2 OR get_byte({column}, 1) <> {"{0}"})
            ORDER BY id
            LIMIT {"{1}"}
            FOR UPDATE
            """;
        _updateSql = $"UPDATE {qualified} SET {column} = {"{0}"} WHERE id = {"{1}"}";
    }

    public string Name { get; }

    public async Task<int> ReencryptBatchAsync(TahsilDbContext context, ISecretProtector protector, int batchSize, CancellationToken cancellationToken)
    {
        var rows = await context.Database
            .SqlQueryRaw<Row>(_selectSql, (int)protector.CurrentKeyId, batchSize)
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            var owner = _boundToRow ? row.Id.ToByteArray(bigEndian: true) : [];
            var reencrypted = protector.Protect(protector.Unprotect(row.Value, _purpose, owner), _purpose, owner);

            await context.Database.ExecuteSqlRawAsync(_updateSql, [reencrypted, row.Id], cancellationToken);
        }

        return rows.Count;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
    private static partial Regex Identifier();

    private sealed record Row(Guid Id, byte[] Value);
}
