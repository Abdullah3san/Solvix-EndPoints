using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Row-Level Security for the billing tables, the plan catalog, and the one-off backfill that gives every
/// pre-existing tenant a subscription (billing is fail-closed: a tenant without one cannot enroll devices).
/// </summary>
/// <remarks>
/// PLACEHOLDER PRICES. The plan names and device limits are product decisions (Starter 25, Business 75,
/// Professional 200, Enterprise custom); the prices below are launch placeholders and must be reviewed by the
/// business before going live. Change them by updating <c>plans</c> as the schema owner (see docs/BILLING.md);
/// existing subscriptions keep the price they bought.
/// </remarks>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261116135200_BillingSecurity")]
public sealed class BillingSecurityMigration : Migration
{
    public static readonly string[] TenantTables =
    [
        "subscriptions",
        "subscription_events",
        "invoices",
        "invoice_lines",
        "payments",
        "billing_profiles",
        "usage_snapshots",
        "invoice_counters",
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in TenantTables)
        {
            RowLevelSecurity.EnableTenantIsolation(migrationBuilder, table);
        }

        // Plan catalog: platform data, writable only by the schema owner. The runtime role may read it but a
        // trigger refuses any change from another role, so no application bug can edit prices or limits.
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION plans_owner_only() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF current_user <> (SELECT tableowner FROM pg_tables WHERE schemaname = 'public' AND tablename = 'plans') THEN
                    RAISE EXCEPTION 'The plan catalog can only be changed by the schema owner' USING ERRCODE = '42501';
                END IF;
                RETURN COALESCE(NEW, OLD);
            END $$;
            CREATE TRIGGER plans_owner_only BEFORE INSERT OR UPDATE OR DELETE ON plans
                FOR EACH ROW EXECUTE FUNCTION plans_owner_only();
            """);

        migrationBuilder.Sql(
            """
            INSERT INTO plans (id, code, name, description, device_limit, monthly_price_minor, annual_price_minor, currency, sort_order, is_public, is_active, created_at_utc, updated_at_utc) VALUES
              ('0197a000-0000-7000-8000-000000000001', 'starter',      'Starter',      'Up to 25 devices',  25,   4900,  49000, 'USD', 10, true, true, now(), now()),
              ('0197a000-0000-7000-8000-000000000002', 'business',     'Business',     'Up to 75 devices',  75,  12900, 129000, 'USD', 20, true, true, now(), now()),
              ('0197a000-0000-7000-8000-000000000003', 'professional', 'Professional', 'Up to 200 devices', 200, 29900, 299000, 'USD', 30, true, true, now(), now()),
              ('0197a000-0000-7000-8000-000000000004', 'enterprise',   'Enterprise',   'Custom device limit and pricing', NULL, NULL, NULL, 'USD', 40, true, true, now(), now());
            """);

        // Backfill (runs as the schema owner; FORCE RLS applies to it too, so use the audited bypass for this transaction only).
        migrationBuilder.Sql(
            """
            SELECT set_config('app.rls_bypass', 'on', true);
            INSERT INTO subscriptions (id, tenant_id, plan_id, plan_code, status, interval, device_limit, unit_price_minor, currency,
                                       trial_ends_at_utc, current_period_start_utc, current_period_end_utc, cancel_at_period_end,
                                       created_at_utc, updated_at_utc)
            SELECT gen_random_uuid(), t.id, p.id, p.code, 'Trialing', 'Monthly', p.device_limit, 0, p.currency,
                   now() + interval '30 days', now(), now() + interval '30 days', false, now(), now()
            FROM tenants t CROSS JOIN plans p
            WHERE p.code = 'starter'
              AND NOT EXISTS (SELECT 1 FROM subscriptions s WHERE s.tenant_id = t.id);
            INSERT INTO subscription_events (id, tenant_id, type, actor, detail, occurred_at_utc, created_at_utc, updated_at_utc)
            SELECT gen_random_uuid(), s.tenant_id, 'TrialStarted', 'System', 'migration backfill: 30-day starter trial', now(), now(), now()
            FROM subscriptions s
            WHERE NOT EXISTS (SELECT 1 FROM subscription_events e WHERE e.tenant_id = s.tenant_id);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("SELECT set_config('app.rls_bypass', 'on', true); DELETE FROM subscription_events; DELETE FROM subscriptions;");
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS plans_owner_only ON plans; DROP FUNCTION IF EXISTS plans_owner_only(); DELETE FROM plans;");
        foreach (var table in TenantTables.Reverse())
        {
            RowLevelSecurity.DisableTenantIsolation(migrationBuilder, table);
        }
    }
}
