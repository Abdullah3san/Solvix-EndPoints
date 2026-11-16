# Billing, plans and device limits

Module: `Solvix.Modules.Billing`. Code map: `Domain/` (plan, subscription, invoice, payment), `Application/`
(entitlement, plan changes, settlement, renewal job, operator service), `Payments/` (Stripe + manual providers),
`Api/` (tenant, webhook and operator controllers).

## Plans

| Code | Name | Devices | Monthly | Annual |
|------|------|---------|---------|--------|
| `starter` | Starter | 25 | **$49.00** | **$490.00** |
| `business` | Business | 75 | **$129.00** | **$1,290.00** |
| `professional` | Professional | 200 | **$299.00** | **$2,990.00** |
| `enterprise` | Enterprise | custom (set per customer) | negotiated | negotiated |

> **⚠ The device limits are the agreed plan design. The prices are PLACEHOLDERS I chose so the system is usable; they
> were not part of the requirements. Confirm or change them before launch.** Annual = 10 × monthly (two months free).

Plans are platform data (table `plans`), not tenant data. They are read-only for the application: a database trigger
rejects any `INSERT/UPDATE/DELETE` from a role other than the schema owner. To change prices, run SQL **as the schema
owner** (the migrator role), e.g.

```sql
UPDATE plans SET monthly_price_minor = 5900, annual_price_minor = 59000 WHERE code = 'starter';
```

Existing subscriptions keep the price and limit they bought (snapshotted at purchase); only new purchases and renewals
of *changed-plan* subscriptions use the new catalog price. A renewal of an unchanged plan uses the customer's stored price.

## Subscription lifecycle

```
register ─► Trialing (starter, TrialDays, default 14)
                │ pay an invoice
                ▼
             Active ──period ends unpaid──► PastDue (grace, default 7 d, still allowed) ──► Expired
                │  ▲  pay renewal invoice (period extended, no gap)
                │  └───────────────────────────────────────────────
                └─cancel (at period end)──► Cancelled         Suspended ◄─► (operator)
```

* **Status is computed from timestamps at request time** (`EntitlementEvaluator`), not from a background job: a stored
  `Active` row whose period ended three months ago is *not* entitled. The renewal worker only tidies persisted state and
  issues invoices.
* Missing subscription ⇒ **not entitled** (fail closed). Existing tenants were given a 30-day Starter trial by the
  migration so nothing breaks on upgrade.
* Plan change: **upgrade** (higher device limit) is immediate *once paid* and credits the unused part of the current period
  (`Proration`); the new period starts at payment. **Downgrade / lateral** is scheduled for the period end and is refused
  if current usage exceeds the target limit; it is re-checked when the renewal invoice is generated (dropped, with an
  audit event, if the fleet no longer fits). Re-selecting the current plan cancels a scheduled change.
* Cancel/resume: `POST /billing/subscription/cancel|resume` (owner). Cancellation applies at the period end.

## Device limits are enforced on the server

`AgentEnrollmentService` calls the entitlement check **inside the enrollment transaction**:

1. a per-tenant PostgreSQL advisory lock (`pg_advisory_xact_lock`) serialises concurrent enrollments for the tenant;
2. `IDeviceEntitlementProvider` evaluates the subscription; not entitled ⇒ HTTP **402** with `subscription_*` code;
3. active (non-retired) devices are counted; at the limit ⇒ HTTP **402** `device_limit_reached`.

Consequences, all covered by integration tests: two agents cannot both take the last slot (8 parallel enrollments at
limit 3 ⇒ exactly 3 succeed); a refused enrollment rolls back and does **not** consume the enrollment token;
re-enrolling hardware that already has a record never needs a free slot; retiring a device frees its slot; existing
devices keep working when a subscription lapses (they are never switched off). The UI shows usage, but **nothing in the
frontend is trusted**: the only gate is the API.

## Invoices and payments

* Invoice numbers are per-tenant and gap-free (`INV-000001`), allocated atomically (`INSERT … ON CONFLICT … RETURNING`).
* Money is `bigint` minor units + currency; tax is `subtotal × basis points`, rounded half away from zero. The tax rate is
  set by the **operator only** (a tenant cannot lower its own tax); default `Solvix:Billing:DefaultTaxRateBasisPoints`.
* An invoice carries an immutable **terms snapshot** (plan, interval, limit, unit price). Settlement applies *that*, never
  request data or the live catalog.
* A unique partial index guarantees at most one live renewal invoice per period (renewal generation is idempotent and safe
  on multiple instances).
* `GET /billing/invoices/{id}/html` returns a printable invoice (Save as PDF). All values are HTML-encoded and the response
  carries `Content-Security-Policy: default-src 'none'`. There is no PDF library dependency.

### Payment providers (`Solvix:Billing:Provider`)

| Provider | Behaviour |
|----------|-----------|
| `manual` (default) | Invoice is issued; the customer pays by bank transfer quoting the number; an **operator** confirms it (`mark-paid`). |
| `stripe` | Hosted Checkout (payment mode). Card data never touches Solvix. Webhooks activate the subscription. |

Stripe setup: set `Provider=stripe`, `Stripe:SecretKey`, `Stripe:WebhookSecret`; create a webhook endpoint
`https://<api-host>/api/v1/billing/webhooks/stripe` subscribed to `checkout.session.completed`,
`checkout.session.async_payment_succeeded`, `checkout.session.async_payment_failed`. **Point it at the API directly**, not at
the web proxy (the signature covers the raw body).

Webhook security (all tested): HMAC-SHA256 `Stripe-Signature` with constant-time compare and a 5-minute timestamp window
(replay defence); body limit 256 KiB (explicit bounded read); events are idempotent (`billing_webhook_events`, unique per
provider event id, in the same transaction as the settlement); the tenant is taken **from our own invoice row**, the signed
metadata must agree or the payment is *not applied* and an alert fires; amount and currency must equal the invoice total;
settlement runs in a scope bound to that single tenant with row-level security enforced; a payment for a void or
already-paid invoice is recorded for refund review, never applied twice; a payment for a **suspended** workspace never lifts
the suspension.

Renewals are **invoice-based**: the worker issues a renewal invoice `RenewalLeadDays` (14) before the period ends and the
owner pays it (or an operator confirms a transfer). Automatic card-on-file charging is *not* implemented (see limits).

### Renewal / expiry worker

`BillingWorker` runs hourly (`Worker:IntervalMinutes`), one instance at a time (advisory lock), per-tenant failures isolated
(`solvix.billing.job_failures`): expire trials, cancel at period end, expire after grace, issue renewal invoices, record the
daily usage peak. It can be disabled (`Worker:Enabled=false`) and is idempotent if run twice.

## Operator API (Enterprise, manual payments, suspension)

Header `X-Operator-Key: <Solvix:Billing:OperatorApiKey>` (≥ 32 chars, constant-time compare, rate limited). **If the key is
not configured the endpoints return 404.** Restrict the route to an internal network at the proxy.

| Method | Path (`/api/v1/platform/billing/tenants/{tenantId}/…`) | Purpose |
|--------|------|---------|
| PUT | `subscription` | Set negotiated terms: `{planCode:"enterprise", interval, deviceLimit, unitPriceMinor, periodEndUtc}` |
| POST | `invoices/{invoiceId}/mark-paid` | Confirm a bank transfer `{reference}` (idempotent) |
| POST | `suspend` / `reactivate` | Block / restore device enrollment |
| PUT | `tax-rate` | `{taxRateBasisPoints}` for this tenant |

Every call names exactly one tenant and runs inside a scope bound to *that* tenant (RLS applies); an invoice id belonging to
another tenant is a 404. All actions are written to `subscription_events` with actor `Operator`.

Example (Enterprise, 500 devices, $1,500/month):

```bash
curl -X PUT "$API/api/v1/platform/billing/tenants/$TENANT/subscription" \
  -H "X-Operator-Key: $SOLVIX_OPERATOR_API_KEY" -H 'Content-Type: application/json' \
  -d '{"planCode":"enterprise","interval":"Monthly","deviceLimit":500,"unitPriceMinor":150000}'
```

## Tenant API (`/api/v1/billing`)

| Method | Path | Who | Purpose |
|--------|------|-----|---------|
| GET | `plans`, `subscription`, `usage` | any member | Catalog, current plan/status/limit, usage + 90-day peak history |
| POST | `subscription/change` `{planCode, interval}` | **owner** | Buy / upgrade (invoice + checkout URL) or schedule a downgrade |
| POST | `subscription/cancel`, `subscription/resume` | owner | Cancel at period end / undo |
| GET | `invoices`, `invoices/{id}`, `invoices/{id}/html`, `history` | owner | Billing data |
| POST | `invoices/{id}/checkout` | owner | New payment session for an open invoice |
| GET/PUT | `profile` | owner | Legal name, e-mail, address, tax id for invoices |

Billing data is **owner-only** (Admins are refused). Plan, status and usage are visible to all members because the UI needs
the limits; they are not sensitive. No card data is stored anywhere; only provider session ids and amounts.

## Reconciling payments (finance)

Alert `SolvixPaymentMismatch` means a provider payment did not match its invoice. Query (as schema owner):

```sql
SELECT p.* FROM payments p JOIN invoices i ON i.id = p.invoice_id AND i.tenant_id = p.tenant_id
WHERE i.status <> 'Paid' OR p.amount_minor <> i.total_minor ORDER BY p.occurred_at_utc DESC;
```

A payment recorded against a `Void` invoice or a second payment on a `Paid` invoice needs a manual refund in the provider
dashboard. A webhook whose amount differed is logged (`Payment for invoice … rejected`) and **not** recorded; settle it by
voiding/re-issuing or confirming via `mark-paid` once the amounts are reconciled.

## Known limits

* Renewal is invoice-based; no stored-card auto-charge, no dunning e-mails (there is no outbound e-mail in the platform
  yet; notify customers from your finance tooling using `GET invoices`).
* Single currency per plan (USD seeded). Prices are not localised; tax is a single rate (no VAT-OSS / jurisdiction logic).
* Refunds and credit notes are handled in the payment provider; Solvix records the payment and shows the invoice as paid.
* A new workspace gets a free trial; there is no abuse control against repeated sign-ups beyond the per-address
  registration rate limit.
