using System.Net;
using Tahsil.Application.Common;

namespace Tahsil.Server.Tests.Authorization;

[Trait("Requirement", "FND-27")]
public sealed class AuthorizerTests
{
    private const string Permission = "tahsil.customers:write";
    private static readonly Guid CustomerId = Guid.CreateVersion7();

    [Fact]
    public void TheSystem_Passes()
    {
        var authorizer = For(SystemCaller.Instance);

        authorizer.Require(Permission).IsSuccess.ShouldBeTrue();
        authorizer.RequireCustomer(CustomerId).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void ApiKeysAndAdmins_PassWithThePermission_AndOnlyWithIt()
    {
        For(new ApiKeyCaller(Guid.CreateVersion7(), new HashSet<string> { Permission })).Require(Permission).IsSuccess.ShouldBeTrue();
        For(new AdminCaller("sub", null, "operator", new HashSet<string> { Permission })).Require(Permission).IsSuccess.ShouldBeTrue();

        var missing = For(new ApiKeyCaller(Guid.CreateVersion7(), new HashSet<string> { "tahsil.customers:read" })).Require(Permission);

        missing.Error.ShouldNotBeNull().Code.ShouldBe("auth.forbidden");
        missing.Error.Message.ShouldContain(Permission);
    }

    [Fact]
    public void Customers_ActOnlyForThemselves_AndHaveNoPermissions()
    {
        var customer = For(new CustomerCaller(CustomerId, CustomerSessionKind.Portal, Guid.CreateVersion7()));

        customer.RequireCustomer(CustomerId).IsSuccess.ShouldBeTrue();
        customer.RequireCustomer(Guid.CreateVersion7()).Error.ShouldNotBeNull().Code.ShouldBe("auth.forbidden");
        customer.Require(Permission).Error.ShouldNotBeNull().Code.ShouldBe("auth.forbidden");
    }

    [Fact]
    public void ApiKeys_CannotActAsACustomer()
    {
        For(new ApiKeyCaller(Guid.CreateVersion7(), new HashSet<string> { Permission })).RequireCustomer(CustomerId).IsFailure.ShouldBeTrue();
    }

    private static Authorizer For(Caller caller) => new(new FixedRequestContext(caller));

    private sealed record FixedRequestContext(Caller Caller) : IRequestContext
    {
        public IPAddress? IpAddress => null;

        public string? UserAgent => null;

        public string? CorrelationId => null;
    }
}
