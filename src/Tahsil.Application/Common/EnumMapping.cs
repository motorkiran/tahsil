namespace Tahsil.Application.Common;

/// <summary>Converts between the domain's enums and the API's, which have the same members.</summary>
internal static class EnumMapping
{
    public static TTarget Map<TTarget>(this Enum value)
        where TTarget : struct, Enum =>
        Enum.Parse<TTarget>(value.ToString());

    public static TTarget? MapOptional<TSource, TTarget>(this TSource? value)
        where TSource : struct, Enum
        where TTarget : struct, Enum =>
        value is { } present ? Enum.Parse<TTarget>(present.ToString()) : null;
}
