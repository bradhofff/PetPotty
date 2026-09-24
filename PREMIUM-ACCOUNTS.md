# Household Premium setup

## Product decision

Premium is owned by a household, not an individual user. The active user is
allowed to use Premium only when they are an active member of a household whose
SQL entitlement is active. If a user belongs to more than one household, the
selected household in the household switcher controls the entitlement.

The first implementation uses a recurring Stripe subscription and Stripe
Checkout. The payer can be any active household member; the subscription and
billing identity are attached to the household. A later billing-management
screen can use the Stripe Billing Portal without changing this entitlement
model.

## Database rollout

Run `Migrationsss/2026-09-23_AddHouseholdPremium.sql` after the household
accounts migration. It adds:

- current household entitlement and Stripe IDs on `Households`;
- `HouseholdPremiumTransactions`, an event/transaction ledger with a unique
  Stripe event ID for webhook idempotency;
- `ApplyHouseholdPremiumStripeEvent`, which records an event and updates the
  entitlement in one serializable transaction.

The entitlement snapshot is intentionally stored on `Households` so feature
checks are cheap. The transaction ledger remains the audit trail for support,
refunds, payment failures, and reconciliation.

## Stripe configuration

Create a recurring Premium Price in Stripe and configure these values using
environment variables, user-secrets, or the deployment secret store. The JSON
settings are intentionally blank in source control.

```text
Stripe__SecretKey=sk_test_...
Stripe__WebhookSecret=whsec_...
Stripe__PremiumPriceId=price_...
App__BaseUrl=https://your-domain.example
```

Register this webhook endpoint in Stripe:

```text
POST https://your-domain.example/stripe/webhook
```

At minimum subscribe it to:

- `checkout.session.completed`
- `customer.subscription.created`
- `customer.subscription.updated`
- `customer.subscription.deleted`
- `invoice.paid`
- `invoice.payment_failed`

Stripe Checkout is created in subscription mode, carries the household public
ID in `client_reference_id` and subscription metadata, and returns the browser
to `/Premium`. The browser return is only a UX message; the webhook is the
source of truth for access.

## Test flow

1. Apply the SQL migration and configure Stripe test-mode values.
2. Run the app over an HTTPS-accessible development URL, or use Stripe CLI to
   forward test events to `/stripe/webhook`.
3. Sign in, open `/Premium`, and start Checkout.
4. Complete Checkout with Stripe's test card `4242 4242 4242 4242` and any
   future expiry/CVC.
5. Confirm the webhook has arrived and `Households.PremiumStatus` is
   `active`.
6. Open `/PremiumTest`. It should render for every active member of that
   household and return `403 Forbidden` for a free household.

The current implementation grants a past-due household access through the
paid period (`PremiumCurrentPeriodEndUtc`) while Stripe retries payment. A
`deleted` subscription immediately becomes non-premium. This policy can be
changed in `HouseholdPremiumStatus.IsPremium` without changing the schema.

## Recommended next steps before launch

- Add a Billing Portal action for owners and a support/admin reconciliation
  view using the transaction ledger.
- Add a scheduled reconciliation job that retrieves active subscriptions from
  Stripe and repairs any missed webhook state.
- Add an explicit refund/chargeback policy and decide whether `past_due` keeps
  access for the full paid period.
- Add an integration test around duplicate events, out-of-order subscription
  events, removed household members, and a user switching between free and
  premium households.
- Add a webhook delivery alert/health check so a disabled endpoint cannot leave
  paid households locked out.

Stripe references: [Create a Checkout Session](https://docs.stripe.com/api/checkout/sessions/create),
[Checkout subscriptions](https://docs.stripe.com/billing/subscriptions/overview), and
[webhook signature verification](https://docs.stripe.com/webhooks/signature).
