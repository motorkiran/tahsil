using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Tahsil.Infrastructure.Persistence.Conventions;

/// <summary>
/// Normalizes every <see cref="DateTimeOffset"/> to UTC on the way to the database, which only stores instants
/// (Npgsql rejects a non-zero offset for <c>timestamptz</c>).
/// </summary>
internal sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => value.ToUniversalTime(),
    value => value.ToUniversalTime());
