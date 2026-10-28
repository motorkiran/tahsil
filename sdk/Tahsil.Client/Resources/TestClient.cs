using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Test mode tools: the clock and simulator cards. Requires <c>tahsil.test:write</c>; the endpoints answer 404 in Live mode.</summary>
public sealed class TestClient
{
    private readonly TahsilHttp _http;

    internal TestClient(TahsilHttp http) => _http = http;

    /// <summary>Read the billing clock.</summary>
    public Task<TestClockResponse> GetClockAsync(RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<TestClockResponse>(HttpMethod.Get, "test/clock", null, options, cancellationToken);

    /// <summary>Travel forward in time. Test mode only: moves the billing clock forward by <c>advanceBy</c> (an ISO 8601 duration such as <c>P1M</c>) or to <c>advanceTo</c>, at most 400 days, and processes everything that falls due on the way, at its own instant. Returns when done.</summary>
    public Task<TimeTravelResponse> AdvanceClockAsync(AdvanceClockRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<TimeTravelResponse>(HttpMethod.Post, "test/clock", request, options, cancellationToken);

    /// <summary>Save a simulator test card for a customer. Test mode only: a saved card without a browser, with a consent record marked as made by the test API.</summary>
    public Task<PaymentMethodResponse> CreateSimulatorPaymentMethodAsync(CreateSimulatorPaymentMethodRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<PaymentMethodResponse>(HttpMethod.Post, "test/simulator/payment-methods", request, options, cancellationToken);

    /// <summary>Change how a simulator card answers. Test mode only: <c>succeed</c>, a failure code such as <c>insufficient_funds</c>, <c>timeout_after_charge</c>, <c>timeout_before_charge</c> or <c>pending</c>.</summary>
    public Task<PaymentMethodResponse> UpdateSimulatorPaymentMethodAsync(Guid id, UpdateSimulatorPaymentMethodRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<PaymentMethodResponse>(HttpMethod.Patch, $"test/simulator/payment-methods/{id}", request, options, cancellationToken);
}
