using Microsoft.EntityFrameworkCore;
using Stripe;
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

    public async Task<Models.Subscription> CreateSubscriptionAsync(User user, SubscriptionRequest req)
    {
        var customer = await GetOrCreateCustomerAsync(user);

        var paymentMethodId = req.PaymentMethodId.StartsWith("pm_")
            ? req.PaymentMethodId
            : (await _paymentMethodService.CreateAsync(new PaymentMethodCreateOptions
            {
                Type = "card",
                Card = new PaymentMethodCardOptions { Token = req.PaymentMethodId }
            })).Id;

        await _paymentMethodService.AttachAsync(paymentMethodId, new PaymentMethodAttachOptions
        {
            Customer = customer.Id
        });

        var stripeSub = await _subscriptionService.CreateAsync(new SubscriptionCreateOptions
        {
            Customer = customer.Id,
            Items = new List<SubscriptionItemOptions>
            {
                new() { Price = _priceId }
            },
            DefaultPaymentMethod = paymentMethodId
        });

        var modelsSubscription = new Models.Subscription
        {
            UserId = user.Id,
            StripeSubscriptionId = stripeSub.Id,
            StripeCustomerId = customer.Id,
            Status = stripeSub.Status,
            CurrentPeriodEnd = GetPeriodEnd(stripeSub),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var existing = await _db.Subscription.FirstOrDefaultAsync(s => s.UserId == user.Id);
        if (existing != null)
        {
            _db.Subscription.Remove(existing);
            await _db.SaveChangesAsync();
        }

        _db.Subscription.Add(modelsSubscription);
        await _db.SaveChangesAsync();

        return modelsSubscription;
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

    private async Task SyncSubscriptionAsync(Stripe.Subscription stripeSub)
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
            _db.Subscription.Add(new Models.Subscription
            {
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
