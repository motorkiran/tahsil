namespace Tahsil.Domain.Money;

/// <summary>
/// An amount in a currency's minor unit: <c>new Money(29900, TRY)</c> is ₺299.00. Arithmetic is checked, and mixing
/// currencies is a bug, so it throws instead of failing quietly.
/// </summary>
public readonly record struct Money(long Amount, Currency Currency)
{
    public static Money Zero(Currency currency) => new(0, currency);

    public static Money operator +(Money left, Money right) => new(checked(left.Amount + right.Amount), Same(left, right));

    public static Money operator -(Money left, Money right) => new(checked(left.Amount - right.Amount), Same(left, right));

    public static Money operator -(Money value) => value with { Amount = checked(-value.Amount) };

    public Money Add(Money other) => this + other;

    public Money Subtract(Money other) => this - other;

    public Money Negate() => -this;

    public override string ToString() => $"{Amount} {Currency.Code} (minor units)";

    private static Currency Same(Money left, Money right) => left.Currency == right.Currency
        ? left.Currency
        : throw new InvalidOperationException($"Cannot combine {left.Currency.Code} and {right.Currency.Code}.");
}
