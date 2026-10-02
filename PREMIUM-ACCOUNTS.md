# Household Premium setup

## Product decision

Premium is owned by a household, not an individual user. The active user is
allowed to use Premium only when they are an active member of a household whose
SQL entitlement is active. If a user belongs to more than one household, the
selected household in the household switcher controls the entitlement.

Monthly and yearly plans use Stripe subscriptions. Lifetime uses a one-time
Stripe Checkout payment. The payer can be any active household member; the
billing identity and Premium entitlement are attached to the household.

## Database rollout

Run `Migrationsss/2026-09-23_AddHouseholdPremium.sql` and then
`Migrationsss/2026-09-25_AddHouseholdPremiumPlanType.sql` after the household
accounts migration. Rerun the September 25 script when deploying the live
checkout changes so its updated stored procedure is installed. The scripts add:

- current household entitlement and Stripe IDs on `Households`;
- `HouseholdPremiumTransactions`, an event/transaction ledger with a unique
  Stripe event ID for webhook idempotency;
- `ApplyHouseholdPremiumStripeEvent`, which records an event and updates the
  entitlement in one serializable transaction.

The entitlement snapshot is intentionally stored on `Households` so feature
checks are cheap. The transaction ledger remains the audit trail for support,
refunds, payment failures, and reconciliation.

## Stripe configuration

Create a one-time Lifetime Price and recurring Monthly and Yearly Prices in
**Stripe live mode**. Test and live Price IDs are different. Commit the three
non-secret live Price IDs in `appsettings.json`. Override them with test Price
IDs in the ignored `appsettings.Development.json` for local testing. Set the
secret key, webhook signing secret, and HTTPS base URL in the VPS environment or
secret store. This VPS loads `/etc/petpotty/petpotty.env` through the `petpotty`
systemd service. The production app refuses Checkout unless the secret key is live
and `App:BaseUrl` is HTTPS; development requires a test key.

```text
Stripe__SecretKey=sk_live_...
Stripe__WebhookSecret=whsec_...
App__BaseUrl=https://your-production-domain.example
```

The **Household Premium** item in the profile icon menu opens the Premium
information tab at `/Profile#premium-settings`, alongside Personal account and
Household. Free households see prices and a button to the standalone `/Purchase`
page for Stripe Checkout. Premium households see their current plan, its price,
renewal or cancellation date, and billing FAQs. `/Purchase` redirects active
Premium households and households with an existing billing subscription to this
tab. For local testing, use **test-mode** keys and
Prices in the ignored `appsettings.Development.json` file. For example:

```json
{
  "Stripe": {
    "SecretKey": "sk_test_...",
    "WebhookSecret": "whsec_...",
    "Plans": {
      "Lifetime": { "Name": "Lifetime", "PriceLabel": "$135 once", "Description": "Lifetime access", "PriceId": "price_...", "Mode": "payment" },
      "Month": { "Name": "Monthly", "PriceLabel": "$6.99 / month", "Description": "Recurring plan", "PriceId": "price_...", "Mode": "subscription" },
      "Year": { "Name": "Yearly", "PriceLabel": "$69.99 / year", "Description": "Recurring plan", "PriceId": "price_...", "Mode": "subscription" }
    }
  }
}
```

Keep existing settings such as `ConnectionStrings` when editing this file.
`App:BaseUrl` should be `http://localhost:5078` for the local HTTP launch profile.
The local file has blank test credential and Price ID slots so it does not
inherit the live Price IDs from the tracked file.

With Stripe CLI connected to the same sandbox as the test key and Prices, run:

```sh
stripe listen --all-snapshot --forward-to http://localhost:5078/stripe/webhook
```

Copy the `whsec_...` signing secret printed by this listener into local
`Stripe:WebhookSecret`, keep the listener running, and restart the app after
editing credentials. The VPS instead uses the signing secret from its live
webhook endpoint in Stripe. An API secret key and a webhook signing secret are
different values; neither belongs in tracked configuration.

Plan keys are internal identifiers; `Name`, `PriceLabel`, and
`Description` are shown on the Premium page. Set `PriceLabel` to match the
corresponding Stripe amount and interval (or state that it is a one-time
purchase). Plan cards remain visible before configuration is complete, but only
plans with a configured `PriceId` can be selected. The selected key
is resolved against server configuration before checkout, so the browser never
chooses an arbitrary Stripe Price ID.
Set `Mode` to `payment` for a one-time price and `subscription` for a recurring
price. Checkout validates and uses this server-side setting. The tracked
`appsettings.json` contains the supplied live Price IDs and displayed plan labels;
`appsettings.Development.json` is ignored by Git and is the per-machine place
for local test credentials and Price IDs.

Create a **live-mode** webhook endpoint in Stripe and register:

```text
POST https://your-production-domain.example/stripe/webhook
```

At minimum subscribe it to:

- `checkout.session.completed`
- `checkout.session.async_payment_succeeded`
- `customer.subscription.created`
- `customer.subscription.updated`
- `customer.subscription.deleted`
- `invoice.paid`
- `invoice.payment_failed`

Checkout carries the household public ID in `client_reference_id` and
subscription metadata. It returns the browser to `/Purchase`. The older
`/Premium` URL also forwards to `/Purchase`. The return page does not grant access; a
signed live-mode webhook updates the SQL entitlement. Verify that Stripe shows
successful webhook deliveries before offering Checkout to customers.

## Test flow

1. Apply both SQL migrations and configure Stripe test-mode values.
2. Run the app over an HTTPS-accessible development URL, or use Stripe CLI to
   forward test events to `/stripe/webhook`.
3. Sign in, choose **Household Premium** from the profile icon menu, and start Checkout on `/Purchase`.
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

## Manage billing and cancellation

Only the subscription purchaser can open **Manage billing**. The app resolves
that purchaser from signed Stripe events in `HouseholdPremiumTransactions` for
the household's current subscription. Active membership is required and checked
again on every portal request. Other members can view the household's status but
cannot open billing or see payment methods and invoices. Missing purchaser
records fail closed; use support reconciliation for historic subscriptions.

The button posts to `/Profile?handler=ManageBilling` with antiforgery protection.
The server reads the customer ID from the household and creates a short-lived
Stripe Customer Portal session. It returns to
`/Profile?billing=return#premium-settings`. Browser-supplied customer IDs and
return URLs are not accepted.

Create a dedicated portal configuration separately in the sandbox and live mode:

- Enable subscription cancellation with mode `at_period_end`.
- Enable payment-method updates and invoice history.
- Disable subscription plan changes and the public portal login page.

Set its returned `bpc_...` ID as `Stripe:BillingPortalConfigurationId`. The local
sandbox ID is in ignored `appsettings.Development.json`. On the VPS set
`Stripe__BillingPortalConfigurationId` in `/etc/petpotty/petpotty.env` to the live
configuration ID. Before opening a session, the app validates that cancellation
is enabled at period end and plan switching is disabled. Missing settings disable
the button. Production requires live credentials and an HTTPS app URL.

The subscription-update webhook records scheduled cancellation and its date.
Premium continues through that paid period. The subscription-deleted webhook
removes access when the subscription ends. Scheduled cancellation also stops
access locally at its recorded end date if the final webhook is delayed. The
parser supports both `cancel_at_period_end` and flexible billing's `cancel_at`.
Lifetime has no recurring subscription or cancellation action.

See [Stripe portal integration](https://docs.stripe.com/customer-management/integrate-customer-portal)
and [portal configuration API](https://docs.stripe.com/api/customer_portal/configurations/create).

## Live rollout check

1. Create the three live Prices with the intended amounts and billing intervals.
   Set the displayed labels in `Stripe:Plans` to match those exact Prices.
2. Deploy the updated app and rerun the September 25 SQL script against the
   production database.
3. Fill the three live Price IDs in `appsettings.json`. Set the live
   key, live webhook signing secret, and HTTPS `App:BaseUrl` in the production
   environment. Restart the app.
4. In Stripe live mode, register `/stripe/webhook` for the events above and
   confirm the endpoint is enabled.
   Create the live portal configuration described above and set its
   `Stripe__BillingPortalConfigurationId` on the VPS.
5. Complete one authorized live purchase. Confirm the corresponding webhook
   delivery succeeded, `Households.PremiumStatus` is `active`, the plan type is
   correct, and `/PremiumTest` opens for an active member of that household.
   Confirm a free household still receives 403.

Local `appsettings.Development.json` is excluded from publish output. On the VPS,
keep the service in the `Production` environment and put live secrets in
`/etc/petpotty/petpotty.env`; do not copy local test settings there. After changing
the environment file or deployed app, run `sudo systemctl restart petpotty`.

## Sandbox verification completed October 1, 2026

Actual hosted Stripe Checkout purchases were completed using Stripe test cards
and isolated test households in the configured development database:

| Plan | Actual sandbox charge | Result |
| --- | --- | --- |
| Yearly | USD 69.99, recurring every year | SQL entitlement active with plan `yearly`; purchase page shows Yearly Premium and a period ending October 1, 2027 |
| Lifetime | USD 135.00, one-time | SQL entitlement active with plan `lifetime`; purchase page shows Lifetime Premium; no subscription ID or period expiry |

For each purchase, another active member inherited Premium, a separate free
household remained free, a non-member had no entitlement, and the completed
Checkout appeared exactly once in the SQL ledger. Scheduling cancellation of
the isolated Yearly sandbox subscription updated `PremiumCancelAtPeriodEnd`
while retaining access through its paid period. The subscription is scheduled
to cancel; the test fixtures are retained for verification. The user's Monthly
household was not modified by these tests.

The originally supplied sandbox Year Price
`price_1UJ9hy7bDJrJnzIvBAkag9Ez` actually billed USD 69.99 **every month**.
It was replaced locally with `price_1ULedk7bDJrJnzIvEvJh3R2j`, which bills
USD 69.99 **every year**. Do not restore the original sandbox Year ID for
annual billing. Month and Lifetime sandbox IDs were correct.

The live Prices already in tracked `appsettings.json` were checked read-only
against Stripe and are all active in live mode with the intended amounts and
intervals: Monthly USD 6.99/month, Yearly USD 69.99/year, Lifetime USD 135 once.
No live payment was made and the VPS has not been deployed from this session.

Testing also exposed and fixed an unbound SQL table alias in
`HouseholdService.EnsurePersonalHousehold`, which prevented a new account's
personal household from being created. Fresh test households now create
successfully.

## Billing management verification completed October 1, 2026

The sandbox portal was configured for payment-method updates, invoice history,
and cancellation at the end of the paid period. The actual **Manage billing**
button opened Stripe's hosted test portal for the isolated Yearly purchaser;
it showed the scheduled October 1, 2027 cancellation and returned to the
Premium tab. No additional subscription changes were made during this check.

Service checks using the signed transaction ledger and a stub Stripe HTTP
handler verified purchaser access; member, non-member, Free, and Lifetime
denials; rejection of unsafe portal settings and redirect URLs; use of the
household's customer ID; the Premium return URL; production rejection of test
credentials; and access before and after a scheduled cancellation's expiry.
These checks do not verify a live portal configuration or VPS deployment.

## Follow-up operations

- Add a support/admin reconciliation view using the transaction ledger.
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
