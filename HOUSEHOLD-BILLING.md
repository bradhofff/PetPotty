# Household billing

Billing is implemented but is **not live**. A checkout return never grants access. The integration must pass the Stripe event acceptance steps below before enabling production checkout.

## Database and application deployment

1. Apply `Migrationsss/2026-09-25_AddHouseholdBilling.sql` to the intended SQL Server database after the existing household migration. It is transactional and rerunnable; it does not select a database or grant existing households paid access.
2. Run `Migrationsss/2026-09-25_VerifyHouseholdBilling.sql`. All failure counts should be zero.
3. Deploy the application. Household settings links to `/Billing`. All active members can view the current plan; only an active Owner can create checkout or a customer portal session. Razor POST forms use antiforgery protection and bind the displayed household's public ID to avoid charging a different household after a tab switches households.
4. Configure Stripe and the public origin as described below. Missing/inconsistent configuration disables checkout and makes the webhook return 503.

The runtime SQL principal needs SELECT/INSERT/UPDATE on `HouseholdBilling`, SELECT/INSERT on `StripeWebhookEvents`, and permission to use `sys.sp_getapplock` / `sys.sp_releaseapplock`. Existing household authorization still reads `HouseholdMembers`. Migration execution needs DDL permissions; the runtime does not.

## Stripe products and prices

These existing **test-mode** prices were read directly from Stripe on 2026-09-25:

| App plan | Product | Price | Required configuration | Finding |
| --- | --- | --- | --- | --- |
| Monthly | `prod_VJnHa9V9lKl1gM` | `price_1UJ9gx7bDJrJnzIveq1yKgC9` | USD 6.99, recurring every **1 month**, fixed amount, quantity 1 | Correct |
| Yearly | `prod_VJnIfPU027heK6` | `price_1UJ9hy7bDJrJnzIvBAkag9Ez` | USD 69.99, recurring every **1 year**, fixed amount, quantity 1 | **Incorrect: existing price recurs every month** |
| Lifetime | `prod_VJnHxqqrRGjXl2` | `price_1UJ9hC7bDJrJnzIvKiEWfr4g` | USD 135.00, **one-time**, quantity 1 | Correct |

In Stripe Product catalog, keep the existing products or name them `PackTracker Monthly`, `PackTracker Yearly`, and `PackTracker Lifetime`. Create a **new** USD 69.99 yearly price on `prod_VJnIfPU027heK6`; set recurring interval to Year, interval count 1. Put that new `price_...` in `Stripe:Plans:Year:PriceId`. The app rejects the existing monthly interval for Yearly. Archive the mistaken price only after checking whether any subscriptions already use it; changing the app setting does not fix those subscriptions.

For production, create the same three products/prices in **live mode** and use their live IDs and key with a separate production database. Test and live objects must not share persisted billing state. There is no Stripe Free product: Free is the default application entitlement.

Do not enable trials, promotion codes, zero-value prices, customer-selected quantities, metered usage, or mixed-interval subscriptions. Checkout requests one card payment method and one configured price. Current paid plans must end before a different plan can be purchased through checkout; portal plan switching is disabled in this release.

## Configuration, without committing secrets

The existing ignored development configuration uses `Stripe:Plans:Month`, `Year`, and `Lifetime`; those names are preserved. `Stripe:BaseUrl` is the canonical externally accessible origin (including a deployment path prefix if needed), never taken from an incoming Host header.

Configure these as deployment environment variables or a secret manager:

```text
Stripe__SecretKey=<sk_test_... for testing; sk_live_... for production>
Stripe__WebhookSecret=<whsec_... for this exact endpoint>
Stripe__BaseUrl=https://YOUR-PUBLIC-APP-HOST
Stripe__LiveMode=false
Stripe__Plans__Month__PriceId=price_1UJ9gx7bDJrJnzIveq1yKgC9
Stripe__Plans__Year__PriceId=<NEW annual test price ID>
Stripe__Plans__Lifetime__PriceId=price_1UJ9hC7bDJrJnzIvKiEWfr4g
Stripe__Plans__Month__PriceLabel=$6.99
Stripe__Plans__Year__PriceLabel=$69.99
Stripe__Plans__Lifetime__PriceLabel=$135
```

Set `Stripe__LiveMode=true` only with the live key and three live price IDs after testing. Never commit the key, webhook signing secret, or populated environment files. The tracked configuration contains only empty IDs, display labels and non-secret defaults. The development file is ignored; on case-sensitive systems ASP.NET expects `appsettings.Development.json`, so deployment environment variables are preferable.

## Stripe Dashboard webhook

In Workbench → Webhooks → Create destination:

- Events from: **Your account**, not connected accounts.
- Destination: **Webhook endpoint**, snapshot events.
- API version: **2025-06-30.basil**. The REST client also pins this version; it retrieves current resources instead of relying on event object shapes.
- URL: **`https://YOUR-PUBLIC-APP-HOST/stripe/webhook`**. The repository has no configured public hostname; replace the host with the same public origin used for `Stripe__BaseUrl`. Do not use `/Billing` or append a trailing slash. Route POST directly to ASP.NET; do not redirect, cache, add browser login, or alter the body. The public endpoint must use HTTPS.
- Copy this destination's signing secret into `Stripe__WebhookSecret`. A Stripe CLI listener has its own different secret.
- Subscribe to precisely these supported events:

```text
checkout.session.completed
checkout.session.async_payment_succeeded
checkout.session.async_payment_failed
checkout.session.expired
invoice.paid
invoice.payment_succeeded
invoice.payment_failed
invoice.payment_action_required
customer.subscription.created
customer.subscription.updated
customer.subscription.deleted
customer.subscription.paused
customer.subscription.resumed
```

Enable normal Stripe automatic retries and monitor deliveries returning non-2xx. Unsupported event types return 200 without changing billing. Bad/missing signatures, stale signatures, wrong test/live mode and malformed supported events return 400; oversized bodies return 413; configuration missing returns 503; persistence, mapping or Stripe API failures return 500 so delivery can be retried.

The endpoint verifies HMAC-SHA256 over the **original bytes**, accepts matching rotated v1 signatures, uses constant-time comparison, and enforces a five-minute timestamp tolerance. Keep the server clock synchronized.

## Stripe customer portal and recurring billing settings

In Settings → Billing → Customer portal, activate the **default** portal configuration for this Stripe mode:

- Enable payment method updates and invoice history.
- Enable subscription cancellation **at the end of the billing period**.
- Disable subscription plan/price switching, quantity changes, trials, coupons and pause collection. Those require additional proration/credit entitlement policy.
- Set the business details, support link and terms/privacy links for your business. Return navigation is supplied by the app as `<Stripe__BaseUrl>/Billing`.
- In Billing → Revenue recovery, enable Smart Retries (or your explicit retry schedule) and choose cancellation after retries are exhausted. The app also handles `past_due` and `unpaid` and never extends access merely because Stripe retries.

Cancellation retains the previously paid entitlement through its paid end date, including an immediate administrative cancellation. A failed renewal has no extra grace period: prior paid time remains, then the plan becomes Free. Lifetime never renews or expires. Refund/dispute revocation and free credit-funded renewals are not automatic entitlement policies in this release; review them manually. The app does not initiate refunds.

## Persistence, ordering and reconciliation

`HouseholdBilling` has one row per billed household and a unique Stripe customer ID. Missing rows resolve to Free, including all newly created households. Its version-1 `BillingJson` stores:

- Stable customer-creation start time for idempotent retry.
- Every checkout attempt's ID, plan, exact price, creation/expiration, original return origin, session ID, payment intent and subscription IDs.
- Each subscription's status, price, latest invoice, period end, scheduled cancellation, cancellation and end dates.
- Deduplicated successful paid invoice/session records: price, plan, PaymentIntent, paid amount/currency, payment and paid coverage dates.

Subscription access is derived from the **paid invoice line's** period, never the invoice's top-level `period_end` or an unpaid subscription's new period. `invoice_payments` must link the invoice to a succeeded PaymentIntent with money received. Zero-value, credit-only or manually paid invoices cannot establish new entitlement. Lifetime additionally requires a paid completed Checkout Session with the expected single price and a succeeded PaymentIntent. Customer IDs and checkout-attempt metadata must match persisted state; no email-based association is used.

Every write obtains a SQL session application lock per household, which also works across multiple app instances. Webhooks fetch Stripe resources **after** obtaining that lock. Old notifications therefore observe current subscription status; paid invoice history is merged by invoice ID, and old subscription records cannot replace another subscription. Lifetime takes precedence over all recurring records. Access is checked against UTC on every read, so it expires even when an expiration event never arrives. `HouseholdBillingCurrent` exposes the effective SQL plan; do not use the cached `HouseholdBilling.PlanName` alone for authorization.

The billing update and `StripeWebhookEvents` receipt commit in one SQL transaction. Duplicate event IDs short-circuit only after a successful commit. API/database failures leave no receipt; Stripe retries safely. No event timestamp comparison is used, avoiding same-second ordering ambiguity. The event audit stores event ID, type, object ID, household, Stripe creation date and processed date without storing raw customer/payment payloads.

Reconciliation procedure:

1. Find the household's customer and inspect `BillingJson` alongside the Stripe customer, sessions, subscriptions and invoice/payment records. Keep the original price IDs and metadata intact.
2. For a delivery failure, correct the configuration/data issue and resend the failed event from Dashboard. For a lost older paid period, resend that invoice's successful event; refreshing a latest invoice alone cannot recover every historical paid interval.
3. Customer creation and Checkout use persisted Stripe idempotency keys. Never blindly repeat an ambiguous external creation beyond the safe retry window. Customer creation is blocked after 23 hours without a saved result; an interrupted session creation is blocked after 25 minutes because its fixed expiration must still be at least 30 minutes away when created. Locate the object in Stripe by `household_id`, `household_public_id` and/or `checkout_attempt_id`, reconcile the persisted ID, then resend its event. Confirm the original operation did not succeed before clearing an abandoned attempt. Do not delete audit receipts to solve a normal retry.
4. For a successfully processed event whose current Stripe state later changed, resend a **newer event** for that resource. Already committed event IDs intentionally do not execute again.

## Automated verification

Verification on 2026-09-25: Release build passed with zero warnings/errors; 22 regression scenarios passed. SQL integration passed against the configured `PetPottyDb_Test`, applying the migration twice and removing its fixtures. The Stripe price lookup was read-only. Actual Stripe-delivered webhook/Checkout acceptance testing is still required; no production deployment or live activation was performed.

From the repository root:

```powershell
dotnet build PetPotty.sln -c Release
dotnet run --project tests/PetPotty.Billing.Tests/PetPotty.Billing.Tests.csproj
```

The dependency-free regression executable exits nonzero on failure. It exercises owner/member/caregiver/unrelated-user access, checkout idempotency and non-activation, signature tampering/replay/rotation, invoice-before-checkout ordering, concurrent retries, historical invoices, renewal failure/recovery, cancellation, expiry, yearly/lifetime payment, foreign customer metadata, and transaction retry semantics.

Optional SQL integration (reads the ignored development connection; **refuses databases not ending in `_Test` or `_BillingTests`**):

```powershell
dotnet run --project tests/PetPotty.Billing.Tests/PetPotty.Billing.Tests.csproj -- --sql
```

This applies the migration twice, checks defaults and expiration, customer uniqueness, receipt rollback and real concurrent SQL application locks. It creates and removes only its own household fixtures; the additive billing migration remains applied.

## Required Stripe acceptance test before launch

1. Use the test database, test key, corrected yearly test price, and `Stripe__LiveMode=false`. For local testing set `Stripe__BaseUrl=http://localhost:5078`. Start the app using the HTTP launch profile.
2. Run `stripe listen --events checkout.session.completed,checkout.session.async_payment_succeeded,checkout.session.async_payment_failed,checkout.session.expired,invoice.paid,invoice.payment_succeeded,invoice.payment_failed,invoice.payment_action_required,customer.subscription.created,customer.subscription.updated,customer.subscription.deleted,customer.subscription.paused,customer.subscription.resumed --forward-to http://localhost:5078/stripe/webhook`. Set `Stripe__WebhookSecret` to the listener's displayed secret and restart the app. Alternatively deploy a test HTTPS endpoint with a Dashboard signing secret.
3. Sign in as an owner, choose Monthly on a Free household, and complete hosted Checkout with Stripe's test card `4242 4242 4242 4242`, a future expiration and any valid CVC/postcode. Verify the successful event's HTTP 200, household mapping, persisted invoice/PaymentIntent, Monthly plan and paid date. Repeat Yearly and Lifetime using separate Free households. A standalone `stripe trigger` uses unrelated customers, so it cannot prove household association.
4. Before successful payment delivery, opening `/Billing?checkout=returned` must still show Free. Abandon and expire a session; retrying must not produce parallel checkouts. Test a declined card; no payment means Free.
5. Resend events, including repeated deliveries in parallel and an old `invoice.payment_failed` after recovery. Receipts and grants must not duplicate, and old events must not regress current state. Temporarily stop the app/database during delivery, restore it, then verify the retry.
6. Use a Stripe test clock with a test customer/household association (or real test subscription renewal) to exercise paid renewal, failed renewal, retry recovery, cancellation at period end and expiration. Keep the application's clock aligned with the simulated date when asserting expiry. Confirm an old canceled subscription event does not affect a replacement subscription, and recurring events cannot replace Lifetime.
7. As Member, Caregiver and an unrelated user, attempt both billing POST handlers and household-ID tampering; expect 403 and no Stripe changes. Verify missing antiforgery tokens are rejected by Razor Pages. Verify missing/invalid webhook signatures return 400 without state changes.
8. Review successful deliveries and SQL verification, then configure a separate live-mode endpoint and live prices. Until this end-to-end sequence has passed, do not describe the integration as live.

References: [Stripe webhooks and delivery behavior](https://docs.stripe.com/webhooks), [invoice payment references](https://docs.stripe.com/api/invoice-payment/object?api-version=2025-06-30.basil), [invoice line coverage periods](https://docs.stripe.com/api/invoice-line-item/object?api-version=2025-06-30.basil).
