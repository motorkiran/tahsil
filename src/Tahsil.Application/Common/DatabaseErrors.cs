using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Tahsil.Application.Common;

internal static class DatabaseErrors
{
    private const string UniqueViolationSqlState = "23505";

    /// <summary>Whether a unique index rejected the change, typically because a concurrent request won a race.</summary>
    public static bool IsUniqueViolation(this DbUpdateException exception) =>
        exception.InnerException is DbException { SqlState: UniqueViolationSqlState };
}
