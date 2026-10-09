using Microsoft.AspNetCore.Components;
using MudBlazor;
using Tahsil.Admin.Security;
using Tahsil.Domain.Common;

namespace Tahsil.Admin.Components.Common;

/// <summary>
/// What admin panel pages share: running use cases as the administrator, saying how they went, and asking before what
/// cannot be undone.
/// </summary>
public abstract class AdminPageBase : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _disposed = new();

    [Inject]
    protected AdminOperations Operations { get; set; } = null!;

    [Inject]
    protected AdminSession Session { get; set; } = null!;

    [Inject]
    protected ISnackbar Snackbar { get; set; } = null!;

    [Inject]
    protected IDialogService Dialogs { get; set; } = null!;

    [Inject]
    protected NavigationManager Navigation { get; set; } = null!;

    /// <summary>Cancelled when the page goes away, such as when the administrator navigates elsewhere.</summary>
    protected CancellationToken Aborted => _disposed.Token;

    /// <summary>Reads through a use case; an error is shown and gives <see langword="null"/>.</summary>
    protected async Task<T?> ReadAsync<THandler, T>(Func<THandler, CancellationToken, Task<Result<T>>> read)
        where THandler : notnull
        where T : class
    {
        var result = await Operations.RunAsync<THandler, Result<T>>(handler => read(handler, Aborted));
        if (result.IsFailure)
        {
            Snackbar.Add(result.Error.Message, Severity.Error);
            return null;
        }

        return result.Value;
    }

    /// <summary>Runs a change and tells the administrator how it went; returns the result's value, or <see langword="null"/>.</summary>
    protected async Task<T?> ChangeAsync<THandler, T>(Func<THandler, CancellationToken, Task<Result<T>>> change, string done)
        where THandler : notnull
        where T : class
    {
        var result = await Operations.RunAsync<THandler, Result<T>>(handler => change(handler, Aborted));
        Report(result.IsSuccess ? null : result.Error, done);
        return result.IsSuccess ? result.Value : null;
    }

    /// <inheritdoc cref="ChangeAsync{THandler, T}"/>
    protected async Task<bool> ChangeAsync<THandler>(Func<THandler, CancellationToken, Task<Result>> change, string done)
        where THandler : notnull
    {
        var result = await Operations.RunAsync<THandler, Result>(handler => change(handler, Aborted));
        Report(result.IsSuccess ? null : result.Error, done);
        return result.IsSuccess;
    }

    protected void Report(Error? error, string done)
    {
        if (error is null)
        {
            Snackbar.Add(done, Severity.Success);
        }
        else
        {
            Snackbar.Add(error.Message, Severity.Error);
        }
    }

    protected async Task<bool> ConfirmAsync(string title, string message, string yes) =>
        await Dialogs.ShowMessageBoxAsync(title, message, yesText: yes, cancelText: "Cancel") == true;

    public void Dispose()
    {
        _disposed.Cancel();
        _disposed.Dispose();
        GC.SuppressFinalize(this);
    }
}
