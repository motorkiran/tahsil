using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tahsil.Application.ApiKeys;
using Tahsil.Application.Auditing;
using Tahsil.Application.Branding;
using Tahsil.Application.Catalog;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Application.Customers;
using Tahsil.Application.Invoices;
using Tahsil.Application.Notifications;
using Tahsil.Application.PaymentMethods;
using Tahsil.Application.Payments;
using Tahsil.Application.Provisioning;
using Tahsil.Application.Subscriptions;
using Tahsil.Application.Webhooks;

namespace Tahsil.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<ServerOptions>()
            .BindConfiguration(ServerOptions.SectionName)
            .Validate(
                options => options.HasValidPublicUrl(),
                $"{ServerOptions.SectionName}:PublicUrl must be an absolute HTTP or HTTPS URL without a query, fragment or user name.")
            .Validate(
                options => options.UsesHttpsWhenRequired(),
                $"{ServerOptions.SectionName}:PublicUrl must use HTTPS unless {ServerOptions.SectionName}:RequireHttps is false.")
            .Validate(
                options => options.RequiresHttpsInLiveMode(),
                $"{ServerOptions.SectionName}:RequireHttps cannot be false in Live mode.")
            .Validate(
                options => options.HasSupportedLocale(),
                $"{ServerOptions.SectionName}:DefaultLocale must be one of: {string.Join(", ", ServerOptions.SupportedLocales)}.")
            .ValidateOnStart();

        services.AddOptions<BrandingOptions>()
            .BindConfiguration(BrandingOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(options => options.HasReadableStylesheet(), $"{BrandingOptions.SectionName}:StylesheetPath does not exist.")
            .ValidateOnStart();

        services.AddSingleton<IPublicUrls, PublicUrls>();
        services.AddScoped<IAuthorizer, Authorizer>();
        services.AddScoped<RequestContextOverride>();

        services.AddOptions<ProvisioningOptions>()
            .BindConfiguration(ProvisioningOptions.SectionName)
            .Validate(options => options.IsValid(), $"{ProvisioningOptions.SectionName}:FilePath does not exist.")
            .ValidateOnStart();
        services.AddScoped<ApplyProvisioningHandler>();
        services.AddScoped<ExportProvisioningHandler>();
        services.AddScoped<IntegrationProvisioning>();
        services.AddScoped<StartupProvisioning>();

        services.AddScoped<ApiKeyAuthenticator>();
        services.AddScoped<CreateApiKeyHandler>();
        services.AddScoped<ListApiKeysHandler>();
        services.AddScoped<GetApiKeyHandler>();
        services.AddScoped<RevokeApiKeyHandler>();
        services.AddScoped<ListAuditEventsHandler>();
        services.AddScoped<CreateTaxRateHandler>();
        services.AddScoped<UpdateTaxRateHandler>();
        services.AddScoped<GetTaxRateHandler>();
        services.AddScoped<ListTaxRatesHandler>();
        services.AddScoped<CreateMeterHandler>();
        services.AddScoped<UpdateMeterHandler>();
        services.AddScoped<GetMeterHandler>();
        services.AddScoped<ListMetersHandler>();
        services.AddScoped<CreateProductHandler>();
        services.AddScoped<UpdateProductHandler>();
        services.AddScoped<DeleteProductHandler>();
        services.AddScoped<IPriceUsage, BillingPriceUsage>();
        services.AddScoped<SubscriptionLines>();
        services.AddScoped<SubscriptionInvoices>();
        services.AddScoped<SubscriptionCycles>();
        services.AddScoped<InvoiceVoiding>();
        services.AddScoped<DraftFinalization>();
        services.AddScoped<ChargeableCards>();
        services.AddScoped<Dunning>();
        services.AddScoped<InvoiceReminders>();
        services.AddScoped<SendInvoiceHandler>();
        services.AddScoped<InvoiceEmailModels>();
        services.AddScoped<ICustomerEmailComposer, InvoiceIssuedComposer>();
        services.AddScoped<ICustomerEmailComposer, InvoiceReminderComposer>();
        services.AddScoped<IOutboxMessageHandler<SendCustomerEmail>, CustomerEmailSender>();
        services.AddScoped<SubscriptionSettlement>();
        services.AddScoped<SubscriptionMapper>();
        services.AddScoped<CreateSubscriptionHandler>();
        services.AddScoped<GetSubscriptionHandler>();
        services.AddScoped<ListSubscriptionsHandler>();
        services.AddScoped<SubscriptionChanges>();
        services.AddScoped<SubscriptionPreviews>();
        services.AddScoped<UpdateSubscriptionHandler>();
        services.AddScoped<PreviewSubscriptionChangeHandler>();
        services.AddScoped<UpcomingInvoiceHandler>();
        services.AddScoped<CancelSubscriptionHandler>();
        services.AddScoped<ResumeSubscriptionHandler>();
        services.AddScoped<GetProductHandler>();
        services.AddScoped<ListProductsHandler>();
        services.AddScoped<CreatePriceHandler>();
        services.AddScoped<UpdatePriceHandler>();
        services.AddScoped<GetPriceHandler>();
        services.AddScoped<ListPricesHandler>();
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<ConnectionSecrets>();
        services.AddSingleton<ConnectionMapper>();
        services.AddSingleton<ProviderAccess>();
        services.AddScoped<ConnectionStartup>();
        services.AddScoped<ListProvidersHandler>();
        services.AddScoped<CreateConnectionHandler>();
        services.AddScoped<UpdateConnectionHandler>();
        services.AddScoped<GetConnectionHandler>();
        services.AddScoped<ListConnectionsHandler>();
        services.AddScoped<DeleteConnectionHandler>();
        services.AddScoped<TestConnectionHandler>();
        services.AddScoped<GetRoutingHandler>();
        services.AddScoped<UpdateRoutingHandler>();
        services.AddOptions<BillingOptions>()
            .BindConfiguration(BillingOptions.SectionName)
            .Validate(options => options.HasKnownTimeZone(), $"{BillingOptions.SectionName}:TimeZone must be an IANA time zone such as Europe/Istanbul.")
            .Validate(options => options.DraftPeriod >= TimeSpan.Zero, $"{BillingOptions.SectionName}:DraftPeriod must not be negative.")
            .ValidateOnStart();
        services.AddOptions<DunningOptions>()
            .BindConfiguration(DunningOptions.SectionName)
            .Validate(options => options.IsValid(), $"{DunningOptions.SectionName}: InvoiceAction is leave_open, mark_uncollectible or void; the retry schedule lists positive offsets in increasing order; FinalReminderBefore is not negative.")
            .ValidateOnStart();
        services.AddOptions<SubscriptionOptions>()
            .BindConfiguration(SubscriptionOptions.SectionName)
            .Validate(options => options.IncompleteExpiry > TimeSpan.Zero, $"{SubscriptionOptions.SectionName}:IncompleteExpiry must be positive.")
            .ValidateOnStart();
        services.AddOptions<InvoiceOptions>()
            .BindConfiguration(InvoiceOptions.SectionName)
            .Validate(options => options.HasValidPrefix(), $"{InvoiceOptions.SectionName}:NumberPrefix must be 1 to 12 capital letters or digits.")
            .Validate(options => options.HasOrderedReminders(), $"{InvoiceOptions.SectionName}:Reminders lists distinct offsets in increasing order.")
            .Validate(options => options.DefaultDaysUntilDue is >= 0 and <= 365, $"{InvoiceOptions.SectionName}:DefaultDaysUntilDue must be between 0 and 365.")
            .ValidateOnStart();
        services.AddSingleton<InvoiceLinks>();
        services.AddScoped<InvoicePages>();
        services.AddScoped<Connections.ProviderSources>();
        services.AddSingleton<InvoiceMapper>();
        services.AddScoped<InvoiceLines>();
        services.AddScoped<InvoiceFinalizer>();
        services.AddScoped<CreateInvoiceHandler>();
        services.AddScoped<AddInvoiceLinesHandler>();
        services.AddScoped<RemoveInvoiceLineHandler>();
        services.AddScoped<UpdateInvoiceHandler>();
        services.AddScoped<DeleteInvoiceHandler>();
        services.AddScoped<FinalizeInvoiceHandler>();
        services.AddScoped<VoidInvoiceHandler>();
        services.AddScoped<MarkInvoiceUncollectibleHandler>();
        services.AddScoped<GetInvoiceHandler>();
        services.AddScoped<ListInvoicesHandler>();
        services.AddScoped<CreateInvoiceItemHandler>();
        services.AddScoped<GetInvoiceItemHandler>();
        services.AddScoped<ListInvoiceItemsHandler>();
        services.AddScoped<DeleteInvoiceItemHandler>();
        services.AddScoped<AdjustBalanceHandler>();
        services.AddScoped<GetBalanceTransactionHandler>();
        services.AddScoped<ListBalanceTransactionsHandler>();
        services.AddScoped<ICustomerHistory, BillingHistory>();
        services.AddOptions<PaymentOptions>()
            .BindConfiguration(PaymentOptions.SectionName)
            .Validate(options => options.IsValid(), $"{PaymentOptions.SectionName}: timeouts must be positive.")
            .ValidateOnStart();
        services.AddScoped<PaymentRequests>();
        services.AddScoped<SavedCards>();
        services.AddScoped<Attention>();
        services.AddScoped<PaymentOutcomes>();
        services.AddScoped<CustomerPayments>();
        services.AddScoped<MerchantPayments>();
        services.AddScoped<PaymentReconciliation>();
        services.AddOptions<RefundOptions>()
            .BindConfiguration(RefundOptions.SectionName)
            .Validate(options => options.IsValid(), $"{RefundOptions.SectionName}:DailyLimitPerActor limits must be positive.")
            .ValidateOnStart();
        services.AddScoped<RefundOutcomes>();
        services.AddScoped<Refunds>();
        services.AddScoped<RefundReconciliation>();
        services.AddScoped<ProviderNotifications>();
        services.AddScoped<ProviderNotificationProcessing>();
        services.AddOptions<WebhookOptions>()
            .BindConfiguration(WebhookOptions.SectionName)
            .Validate(options => options.IsValid(), $"{WebhookOptions.SectionName}: intervals and delays must be positive, the batch size between 1 and 100 and the endpoint concurrency between 1 and 64.")
            .ValidateOnStart();
        services.AddScoped<IOutboxMessageHandler<WebhookEventPublished>, WebhookFanOut>();
        services.AddScoped<ListEventTypesHandler>();
        services.AddScoped<ListWebhookEndpointsHandler>();
        services.AddScoped<GetWebhookEndpointHandler>();
        services.AddScoped<CreateWebhookEndpointHandler>();
        services.AddScoped<UpdateWebhookEndpointHandler>();
        services.AddScoped<DeleteWebhookEndpointHandler>();
        services.AddScoped<RotateWebhookSecretHandler>();
        services.AddScoped<SendTestWebhookHandler>();
        services.AddScoped<ListWebhookDeliveriesHandler>();
        services.AddScoped<GetWebhookDeliveryHandler>();
        services.AddScoped<RedeliverWebhookHandler>();
        services.AddScoped<IOutboxMessageHandler<RefundLateSuccess>, LateSuccessRefunder>();
        services.AddOptions<Notifications.NotificationOptions>().BindConfiguration(Notifications.NotificationOptions.SectionName);
        services.AddScoped<IOutboxMessageHandler<Notifications.SendOrderConfirmation>, Notifications.OrderConfirmationSender>();
        services.AddScoped<CreateRefundHandler>();
        services.AddScoped<GetRefundHandler>();
        services.AddScoped<ListRefundsHandler>();
        services.AddScoped<PayInvoiceHandler>();
        services.AddScoped<GetPaymentHandler>();
        services.AddScoped<ListPaymentsHandler>();
        services.AddScoped<IConnectionUsage, PaymentConnectionUsage>();
        services.AddScoped<ListPaymentMethodsHandler>();
        services.AddScoped<GetPaymentMethodHandler>();
        services.AddScoped<SetDefaultPaymentMethodHandler>();
        services.AddScoped<RemovePaymentMethodHandler>();
        services.AddScoped<PaymentMethodExpiry>();
        services.AddScoped<IOutboxMessageHandler<DeleteProviderToken>, ProviderTokenDeleter>();
        services.AddScoped<CreateTestPaymentMethodHandler>();
        services.AddOptions<Checkout.MerchantOptions>().BindConfiguration(Checkout.MerchantOptions.SectionName);
        services.AddOptions<Checkout.CheckoutOptions>().BindConfiguration(Checkout.CheckoutOptions.SectionName).ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<Checkout.CheckoutOptions>, Checkout.CheckoutOptionsValidation>();
        services.AddScoped<Checkout.LegalDocumentModels>();
        services.AddScoped<Checkout.LegalDocumentVersions>();
        services.AddOptions<Checkout.CheckoutPageOptions>().BindConfiguration(Checkout.CheckoutPageOptions.SectionName);
        services.AddScoped<Checkout.CheckoutCompletion>();
        services.AddScoped<Checkout.CheckoutProtection>();
        services.AddScoped<Checkout.CheckoutPages>();
        services.AddScoped<Checkout.CheckoutMapper>();
        services.AddScoped<Checkout.CheckoutExpiry>();
        services.AddScoped<Checkout.CreateCheckoutSessionHandler>();
        services.AddScoped<Checkout.GetCheckoutSessionHandler>();
        services.AddScoped<Checkout.ListCheckoutSessionsHandler>();
        services.AddScoped<Checkout.ExpireCheckoutSessionHandler>();
        services.AddScoped<AttentionItems.ListAttentionItemsHandler>();
        services.AddScoped<AttentionItems.ListWorkerAttentionHandler>();
        services.AddScoped<AttentionItems.ResolveAttentionItemHandler>();
        services.AddScoped<AttentionItems.ResolvePaymentHandler>();
        services.AddScoped<TestClock.GetTestClockHandler>();
        services.AddScoped<TestClock.AdvanceTestClockHandler>();
        services.AddScoped<UpdateTestPaymentMethodHandler>();
        services.AddScoped<CustomerMapper>();
        services.AddScoped<CreateCustomerHandler>();
        services.AddScoped<UpsertCustomerHandler>();
        services.AddScoped<UpdateCustomerHandler>();
        services.AddScoped<GetCustomerHandler>();
        services.AddScoped<ListCustomersHandler>();
        services.AddScoped<DeleteCustomerHandler>();
        services.AddScoped<ExportCustomerHandler>();

        return services;
    }
}
