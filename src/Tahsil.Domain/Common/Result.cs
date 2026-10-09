using System.Diagnostics.CodeAnalysis;

namespace Tahsil.Domain.Common;

/// <summary>The outcome of an operation that can fail in an expected way. Unexpected failures stay exceptions.</summary>
public class Result
{
    protected Result(Error? error) => Error = error;

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => Error is not null;

    public static Result Success() => new(null);

    /// <summary>A success with a value; for values of an interface type, which implicit conversions do not cover.</summary>
    public static Result<TValue> Success<TValue>(TValue value) => new(value);

    public static Result Failure(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));

    public static implicit operator Result(Error error) => Failure(error);
}

public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue value)
        : base(null) => _value = value;

    private Result(Error error)
        : base(error ?? throw new ArgumentNullException(nameof(error)))
    {
    }

    public TValue Value => IsSuccess ? _value! : throw new InvalidOperationException("A failed result has no value.");

    public static implicit operator Result<TValue>(TValue value) => new(value);

    public static implicit operator Result<TValue>(Error error) => new(error);
}
