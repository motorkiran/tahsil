using System.Collections.Frozen;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Tahsil.Infrastructure.Persistence.Conventions;

/// <summary>Stores an enum as its snake_case name, and fails on a name it does not know instead of guessing.</summary>
internal sealed class SnakeCaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    value => ToName(value),
    name => ToValue(name))
    where TEnum : struct, Enum
{
    private static readonly FrozenDictionary<TEnum, string> Names =
        Enum.GetValues<TEnum>().ToFrozenDictionary(value => value, DbEnum.Value);

    private static readonly FrozenDictionary<string, TEnum> Values =
        Names.ToFrozenDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    private static string ToName(TEnum value) => Names.TryGetValue(value, out var name)
        ? name
        : throw new InvalidOperationException($"{value} is not a defined {typeof(TEnum).Name} value.");

    private static TEnum ToValue(string name) => Values.TryGetValue(name, out var value)
        ? value
        : throw new InvalidOperationException($"'{name}' is not a stored {typeof(TEnum).Name} value.");
}
