using Microsoft.EntityFrameworkCore;
using Stripe;
using Stripe.Checkout;
using WebApplication1.Data;
using WebApplication1.DTOs;
using WebApplication1.Models;

namespace WebApplication1.Services;

public class StripeService
{
    private readonly UserDB _db;
    private readonly CustomerService _customerService;
    private readonly PaymentMethodService _paymentMethodService;
    private readonly Stripe.SubscriptionService _subscriptionService;
    private readonly string _priceId;

    public StripeService(UserDB db, IConfiguration config)
    {
        _db = db;
        StripeConfiguration.ApiKey = config["Stripe:SecretKey"];
        _customerService = new CustomerService();
        _paymentMethodService = new PaymentMethodService();
        _subscriptionService = new Stripe.SubscriptionService();
        _priceId = config["Stripe:PriceId"]!;
    }

    private static DateTime GetPeriodEnd(Stripe.Subscription sub)
    {
        if (sub.Items?.Data?.Count > 0)
        {
            var item = sub.Items.Data[0];
            if (item.CurrentPeriodEnd != default)
                return item.CurrentPeriodEnd;
        }

        var ts = sub.RawJObject?["current_period_end"]?.ToObject<long>();
        return ts.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(ts.Value).UtcDateTime
            : DateTime.UtcNow.AddDays(30);
    }

    private static string? GetInvoiceSubscriptionId(Invoice invoice)
    {
        return invoice.RawJObject?["subscription"]?.ToObject<string>();
    }

    public async Task<Customer> GetOrCreateCustomerAsync(User user)
    {
        if (!string.IsNullOrEmpty(user.StripeCustomerId))
        {
            return await _customerService.GetAsync(user.StripeCustomerId);
        }

        var customer = await _customerService.CreateAsync(new CustomerCreateOptions
        {
            Email = user.email,
            Name = user.username
        });

        user.StripeCustomerId = customer.Id;
        await _db.SaveChangesAsync();

        return customer;
    }

    public async Task<string> CreateCheckoutSessionAsync(User user, string successUrl, string cancelUrl)
    {
        var customer = await GetOrCreateCustomerAsync(user);

        var options = new SessionCreateOptions
        {
            Customer = customer.Id,
            ClientReferenceId = user.Id.ToString(),
            Mode = "subscription",
            LineItems = new List<SessionLineItemOptions>
            {
                new() { Price = _priceId, Quantity = 1 }
            },
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl
        };

        var sessionService = new Stripe.Checkout.SessionService();
        var session = await sessionService.CreateAsync(options);

        return session.Url!;
    }

    public async Task<Models.Subscription?> VerifyCheckoutSessionAsync(int userId, string sessionId)
    {
        var sessionService = new Stripe.Checkout.SessionService();
        var session = await sessionService.GetAsync(sessionId, new SessionGetOptions
        {
            Expand = new List<string> { "subscription" }
        });

        if (session?.SubscriptionId == null)
            return null;

        var stripeSub = session.Subscription as Stripe.Subscription
            ?? await _subscriptionService.GetAsync(session.SubscriptionId);

        var local = await _db.Subscription
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSub.Id)
            ?? await _db.Subscription
            .FirstOrDefaultAsync(s => s.UserId == userId);

        if (local != null)
        {
            local.Status = stripeSub.Status;
            local.CurrentPeriodEnd = GetPeriodEnd(stripeSub);
            local.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            local = new Models.Subscription
            {
                UserId = userId,
                StripeSubscriptionId = stripeSub.Id,
                StripeCustomerId = stripeSub.CustomerId,
                Status = stripeSub.Status,
                CurrentPeriodEnd = GetPeriodEnd(stripeSub),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.Subscription.Add(local);
        }

        await _db.SaveChangesAsync();
        return local;
    }

    public async Task CancelSubscriptionAsync(string stripeSubscriptionId)
    {
        await _subscriptionService.CancelAsync(stripeSubscriptionId);

        var local = await _db.Subscription
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId);
        if (local != null)
        {
            local.Status = "canceled";
            local.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task HandleWebhookAsync(string json, string signatureHeader, string webhookSecret)
    {
        var stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, webhookSecret);

        switch (stripeEvent.Type)
        {
            case "checkout.session.completed":
                var checkoutSession = stripeEvent.Data.Object as Session;
                if (checkoutSession?.SubscriptionId != null)
                {
                    var checkoutSub = await _subscriptionService.GetAsync(checkoutSession.SubscriptionId);
                    await SyncSubscriptionAsync(checkoutSub, checkoutSession.ClientReferenceId);
                }
                break;

            case "customer.subscription.updated":
            case "customer.subscription.deleted":
                var sub = stripeEvent.Data.Object as Stripe.Subscription;
                if (sub != null)
                {
                    await SyncSubscriptionAsync(sub);
                }
                break;

            case "invoice.payment_succeeded":
                var invoice = stripeEvent.Data.Object as Invoice;
                var subId = invoice != null ? GetInvoiceSubscriptionId(invoice) : null;
                if (subId != null)
                {
                    var updatedSub = await _subscriptionService.GetAsync(subId);
                    await SyncSubscriptionAsync(updatedSub);
                }
                break;

            case "invoice.payment_failed":
                var failedInvoice = stripeEvent.Data.Object as Invoice;
                var failedSubId = failedInvoice != null ? GetInvoiceSubscriptionId(failedInvoice) : null;
                if (failedSubId != null)
                {
                    var failedSub = await _subscriptionService.GetAsync(failedSubId);
                    await SyncSubscriptionAsync(failedSub);
                }
                break;
        }
    }

    private async Task SyncSubscriptionAsync(Stripe.Subscription stripeSub, string? userIdFromCheckout = null)
    {
        var local = await _db.Subscription
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSub.Id);

        if (local != null)
        {
            local.Status = stripeSub.Status;
            local.CurrentPeriodEnd = GetPeriodEnd(stripeSub);
            local.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            var userId = userIdFromCheckout != null && int.TryParse(userIdFromCheckout, out var parsed)
                ? parsed : 0;

            _db.Subscription.Add(new Models.Subscription
            {
                UserId = userId,
                StripeSubscriptionId = stripeSub.Id,
                StripeCustomerId = stripeSub.CustomerId,
                Status = stripeSub.Status,
                CurrentPeriodEnd = GetPeriodEnd(stripeSub),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();
    }
}
