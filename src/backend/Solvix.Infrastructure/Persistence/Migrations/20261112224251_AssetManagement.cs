using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssetManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "serial_number_key",
                table: "devices",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                computedColumnSql: "upper(btrim(serial_number))",
                stored: true);

            migrationBuilder.CreateTable(
                name: "asset_audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    changes_json = table.Column<string>(type: "jsonb", nullable: true),
                    actor_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_audit_log", x => x.id);
                    table.ForeignKey(
                        name: "fk_asset_audit_log_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "asset_physical_audits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_physical_audits", x => x.id);
                    table.UniqueConstraint("ak_asset_physical_audits_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_asset_physical_audits_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "asset_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_prefix = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    default_currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    auto_approve_high_confidence_matches = table.Column<bool>(type: "boolean", nullable: false),
                    warranty_alert_days = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_settings", x => x.id);
                    table.CheckConstraint("ck_asset_settings_warranty_alert_days", "warranty_alert_days BETWEEN 1 AND 365");
                    table.ForeignKey(
                        name: "fk_asset_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "asset_tag_sequences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_value = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_tag_sequences", x => x.id);
                    table.CheckConstraint("ck_asset_tag_sequences_positive", "last_value >= 0");
                    table.ForeignKey(
                        name: "fk_asset_tag_sequences_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "asset_vendors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    contact_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    website = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    account_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_vendors", x => x.id);
                    table.UniqueConstraint("ak_asset_vendors_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_asset_vendors_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_tag = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    condition = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    manufacturer = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    manufacturer_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    serial_number = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    serial_number_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assignee_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    employee_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    assigned_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    vendor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purchase_date = table.Column<DateOnly>(type: "date", nullable: true),
                    purchase_cost = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    purchase_order_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    invoice_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    warranty_start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    warranty_end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    warranty_provider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    warranty_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    depreciation_method = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    useful_life_months = table.Column<int>(type: "integer", nullable: true),
                    salvage_value = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    status_before_maintenance = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    last_audited_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retired_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retirement_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    disposed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disposal_method = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    disposal_value = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    disposal_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    disposal_notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    // "version" maps to the PostgreSQL xmin system column (optimistic concurrency); it is not created.
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.id);
                    table.UniqueConstraint("ak_assets_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_assets_amounts_non_negative", "(purchase_cost IS NULL OR purchase_cost >= 0) AND (salvage_value IS NULL OR salvage_value >= 0) AND (disposal_value IS NULL OR disposal_value >= 0)");
                    table.CheckConstraint("ck_assets_depreciation_inputs", "depreciation_method = 'None' OR (useful_life_months IS NOT NULL AND purchase_cost IS NOT NULL AND purchase_date IS NOT NULL)");
                    table.CheckConstraint("ck_assets_disposed_has_details", "status <> 'Disposed' OR (disposed_at_utc IS NOT NULL AND disposal_method IS NOT NULL)");
                    table.CheckConstraint("ck_assets_in_use_is_assigned", "status <> 'InUse' OR assigned_user_id IS NOT NULL OR assignee_name IS NOT NULL");
                    table.CheckConstraint("ck_assets_retired_has_date", "status NOT IN ('Retired', 'Disposed') OR retired_at_utc IS NOT NULL");
                    table.CheckConstraint("ck_assets_salvage_within_cost", "salvage_value IS NULL OR purchase_cost IS NULL OR salvage_value <= purchase_cost");
                    table.CheckConstraint("ck_assets_single_assignee", "assigned_user_id IS NULL OR assignee_name IS NULL");
                    table.CheckConstraint("ck_assets_tag_format", "asset_tag = upper(asset_tag) AND length(asset_tag) BETWEEN 1 AND 40");
                    table.CheckConstraint("ck_assets_useful_life", "useful_life_months IS NULL OR useful_life_months BETWEEN 1 AND 600");
                    table.CheckConstraint("ck_assets_warranty_period", "warranty_start_date IS NULL OR warranty_end_date IS NULL OR warranty_end_date >= warranty_start_date");
                    table.ForeignKey(
                        name: "fk_assets_asset_vendors_tenant_id_vendor_id",
                        columns: x => new { x.tenant_id, x.vendor_id },
                        principalTable: "asset_vendors",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_assets_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "asset_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assignee_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    employee_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expected_return_date = table.Column<DateOnly>(type: "date", nullable: true),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    returned_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    returned_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    return_notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    return_condition = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_assignments", x => x.id);
                    table.CheckConstraint("ck_asset_assignments_assignee", "assigned_user_id IS NOT NULL OR assignee_name IS NOT NULL");
                    table.CheckConstraint("ck_asset_assignments_period", "returned_at_utc IS NULL OR returned_at_utc >= assigned_at_utc");
                    table.ForeignKey(
                        name: "fk_asset_assignments_assets_tenant_id_asset_id",
                        columns: x => new { x.tenant_id, x.asset_id },
                        principalTable: "assets",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_asset_assignments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "asset_device_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    confidence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    is_ambiguous = table.Column<bool>(type: "boolean", nullable: false),
                    evidence_json = table.Column<string>(type: "jsonb", nullable: false),
                    detected_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_evaluated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_device_links", x => x.id);
                    table.ForeignKey(
                        name: "fk_asset_device_links_assets_tenant_id_asset_id",
                        columns: x => new { x.tenant_id, x.asset_id },
                        principalTable: "assets",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_asset_device_links_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "asset_maintenance_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    vendor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    performed_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    cost = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    scheduled_for = table.Column<DateOnly>(type: "date", nullable: true),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_maintenance_records", x => x.id);
                    table.CheckConstraint("ck_asset_maintenance_records_cost", "cost IS NULL OR cost >= 0");
                    table.ForeignKey(
                        name: "fk_asset_maintenance_records_asset_vendors_tenant_id_vendor_id",
                        columns: x => new { x.tenant_id, x.vendor_id },
                        principalTable: "asset_vendors",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_asset_maintenance_records_assets_tenant_id_asset_id",
                        columns: x => new { x.tenant_id, x.asset_id },
                        principalTable: "assets",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_asset_maintenance_records_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "asset_physical_audit_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    physical_audit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected = table.Column<bool>(type: "boolean", nullable: false),
                    result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    expected_branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expected_location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    observed_location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    observed_condition = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    scanned_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scanned_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_physical_audit_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_asset_physical_audit_items_asset_physical_audits_tenant_id_~",
                        columns: x => new { x.tenant_id, x.physical_audit_id },
                        principalTable: "asset_physical_audits",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_asset_physical_audit_items_assets_tenant_id_asset_id",
                        columns: x => new { x.tenant_id, x.asset_id },
                        principalTable: "assets",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_asset_physical_audit_items_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "asset_transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    to_branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    transferred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    transferred_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_transfers", x => x.id);
                    table.ForeignKey(
                        name: "fk_asset_transfers_assets_tenant_id_asset_id",
                        columns: x => new { x.tenant_id, x.asset_id },
                        principalTable: "assets",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_asset_transfers_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_devices_tenant_id_serial_number_key",
                table: "devices",
                columns: new[] { "tenant_id", "serial_number_key" },
                filter: "serial_number_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_asset_assignments_tenant_id_asset_id_assigned_at_utc",
                table: "asset_assignments",
                columns: new[] { "tenant_id", "asset_id", "assigned_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_asset_assignments_tenant_id_assigned_user_id",
                table: "asset_assignments",
                columns: new[] { "tenant_id", "assigned_user_id" },
                filter: "assigned_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_asset_assignments_open",
                table: "asset_assignments",
                columns: new[] { "tenant_id", "asset_id" },
                unique: true,
                filter: "returned_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_asset_audit_log_tenant_id_asset_id_occurred_at_utc",
                table: "asset_audit_log",
                columns: new[] { "tenant_id", "asset_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_asset_audit_log_tenant_id_entity_type_entity_id",
                table: "asset_audit_log",
                columns: new[] { "tenant_id", "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_asset_audit_log_tenant_id_occurred_at_utc",
                table: "asset_audit_log",
                columns: new[] { "tenant_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_asset_device_links_tenant_id_asset_id_device_id",
                table: "asset_device_links",
                columns: new[] { "tenant_id", "asset_id", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_asset_device_links_tenant_id_status_detected_at_utc",
                table: "asset_device_links",
                columns: new[] { "tenant_id", "status", "detected_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_asset_device_links_approved_asset",
                table: "asset_device_links",
                columns: new[] { "tenant_id", "asset_id" },
                unique: true,
                filter: "status = 'Approved'");

            migrationBuilder.CreateIndex(
                name: "ux_asset_device_links_approved_device",
                table: "asset_device_links",
                columns: new[] { "tenant_id", "device_id" },
                unique: true,
                filter: "status = 'Approved'");

            migrationBuilder.CreateIndex(
                name: "ix_asset_maintenance_records_tenant_id_asset_id_created_at_utc",
                table: "asset_maintenance_records",
                columns: new[] { "tenant_id", "asset_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_asset_maintenance_records_tenant_id_status_scheduled_for",
                table: "asset_maintenance_records",
                columns: new[] { "tenant_id", "status", "scheduled_for" });

            migrationBuilder.CreateIndex(
                name: "ix_asset_maintenance_records_tenant_id_vendor_id",
                table: "asset_maintenance_records",
                columns: new[] { "tenant_id", "vendor_id" });

            migrationBuilder.CreateIndex(
                name: "ux_asset_maintenance_records_in_progress",
                table: "asset_maintenance_records",
                columns: new[] { "tenant_id", "asset_id" },
                unique: true,
                filter: "status = 'InProgress'");

            migrationBuilder.CreateIndex(
                name: "ix_asset_physical_audit_items_tenant_id_asset_id",
                table: "asset_physical_audit_items",
                columns: new[] { "tenant_id", "asset_id" });

            migrationBuilder.CreateIndex(
                name: "ix_asset_physical_audit_items_tenant_id_physical_audit_id_asse~",
                table: "asset_physical_audit_items",
                columns: new[] { "tenant_id", "physical_audit_id", "asset_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_asset_physical_audit_items_tenant_id_physical_audit_id_resu~",
                table: "asset_physical_audit_items",
                columns: new[] { "tenant_id", "physical_audit_id", "result" });

            migrationBuilder.CreateIndex(
                name: "ix_asset_physical_audits_tenant_id_status_started_at_utc",
                table: "asset_physical_audits",
                columns: new[] { "tenant_id", "status", "started_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_asset_settings_tenant_id",
                table: "asset_settings",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_asset_tag_sequences_tenant_id_scope",
                table: "asset_tag_sequences",
                columns: new[] { "tenant_id", "scope" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_asset_transfers_tenant_id_asset_id_transferred_at_utc",
                table: "asset_transfers",
                columns: new[] { "tenant_id", "asset_id", "transferred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_asset_vendors_tenant_id_normalized_name",
                table: "asset_vendors",
                columns: new[] { "tenant_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_assigned_user_id",
                table: "assets",
                columns: new[] { "tenant_id", "assigned_user_id" },
                filter: "assigned_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_category",
                table: "assets",
                columns: new[] { "tenant_id", "category" });

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_serial_number_key",
                table: "assets",
                columns: new[] { "tenant_id", "serial_number_key" },
                filter: "serial_number_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_status_category",
                table: "assets",
                columns: new[] { "tenant_id", "status", "category" });

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_vendor_id",
                table: "assets",
                columns: new[] { "tenant_id", "vendor_id" },
                filter: "vendor_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_warranty_end_date",
                table: "assets",
                columns: new[] { "tenant_id", "warranty_end_date" },
                filter: "warranty_end_date IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_assets_tenant_id_asset_tag",
                table: "assets",
                columns: new[] { "tenant_id", "asset_tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_assets_tenant_id_manufacturer_key_serial_number_key",
                table: "assets",
                columns: new[] { "tenant_id", "manufacturer_key", "serial_number_key" },
                unique: true,
                filter: "serial_number_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asset_assignments");

            migrationBuilder.DropTable(
                name: "asset_audit_log");

            migrationBuilder.DropTable(
                name: "asset_device_links");

            migrationBuilder.DropTable(
                name: "asset_maintenance_records");

            migrationBuilder.DropTable(
                name: "asset_physical_audit_items");

            migrationBuilder.DropTable(
                name: "asset_settings");

            migrationBuilder.DropTable(
                name: "asset_tag_sequences");

            migrationBuilder.DropTable(
                name: "asset_transfers");

            migrationBuilder.DropTable(
                name: "asset_physical_audits");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropTable(
                name: "asset_vendors");

            migrationBuilder.DropIndex(
                name: "ix_devices_tenant_id_serial_number_key",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "serial_number_key",
                table: "devices");
        }
    }
}
