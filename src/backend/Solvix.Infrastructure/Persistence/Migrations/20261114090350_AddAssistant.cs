using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistant : Migration
    {
        /// <summary>Tenant-owned tables created by this migration; each gets FORCEd Row-Level Security.</summary>
        public static readonly string[] TenantTables =
        [
            "assistant_audit_log",
            "assistant_conversations",
            "assistant_messages",
            "assistant_proposals",
            "assistant_reports",
            "assistant_ticket_counters",
            "assistant_tickets",
            "knowledge_chunks",
            "knowledge_documents",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assistant_audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    input_json = table.Column<string>(type: "text", nullable: true),
                    summary = table.Column<string>(type: "text", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assistant_audit_log", x => x.id);
                    table.ForeignKey(
                        name: "fk_assistant_audit_log_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "assistant_conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    last_message_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assistant_conversations", x => x.id);
                    table.UniqueConstraint("ak_assistant_conversations_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_assistant_conversations_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "assistant_proposals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    proposed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposed_by_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    risk = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    parameters_json = table.Column<string>(type: "jsonb", nullable: false),
                    targets_json = table.Column<string>(type: "jsonb", nullable: false),
                    target_count = table.Column<int>(type: "integer", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_by_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    executed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assistant_proposals", x => x.id);
                    table.ForeignKey(
                        name: "fk_assistant_proposals_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "assistant_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    content_markdown = table.Column<string>(type: "text", nullable: false),
                    parameters_json = table.Column<string>(type: "jsonb", nullable: false),
                    narrative_included = table.Column<bool>(type: "boolean", nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    generated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generated_by_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assistant_reports", x => x.id);
                    table.ForeignKey(
                        name: "fk_assistant_reports_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "assistant_ticket_counters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_number = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assistant_ticket_counters", x => x.id);
                    table.ForeignKey(
                        name: "fk_assistant_ticket_counters_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "assistant_tickets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    device_refs_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    created_via_assistant = table.Column<bool>(type: "boolean", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assistant_tickets", x => x.id);
                    table.ForeignKey(
                        name: "fk_assistant_tickets_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    chunk_count = table.Column<int>(type: "integer", nullable: false),
                    character_count = table.Column<int>(type: "integer", nullable: false),
                    redaction_count = table.Column<int>(type: "integer", nullable: false),
                    embedding_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    extracted_text = table.Column<string>(type: "text", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_documents", x => x.id);
                    table.UniqueConstraint("ak_knowledge_documents_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_knowledge_documents_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "assistant_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    sources_json = table.Column<string>(type: "jsonb", nullable: true),
                    tool_calls_json = table.Column<string>(type: "jsonb", nullable: true),
                    artifacts_json = table.Column<string>(type: "jsonb", nullable: true),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assistant_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_assistant_messages_assistant_conversations_tenant_id_conver~",
                        columns: x => new { x.tenant_id, x.conversation_id },
                        principalTable: "assistant_conversations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_assistant_messages_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_chunks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    section = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    page = table.Column<int>(type: "integer", nullable: true),
                    embedding = table.Column<float[]>(type: "real[]", nullable: false),
                    embedding_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_chunks", x => x.id);
                    table.ForeignKey(
                        name: "fk_knowledge_chunks_knowledge_documents_tenant_id_document_id",
                        columns: x => new { x.tenant_id, x.document_id },
                        principalTable: "knowledge_documents",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_knowledge_chunks_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assistant_audit_log_tenant_id_created_at_utc",
                table: "assistant_audit_log",
                columns: new[] { "tenant_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_assistant_audit_log_tenant_id_kind",
                table: "assistant_audit_log",
                columns: new[] { "tenant_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_assistant_audit_log_tenant_id_proposal_id",
                table: "assistant_audit_log",
                columns: new[] { "tenant_id", "proposal_id" });

            migrationBuilder.CreateIndex(
                name: "ix_assistant_conversations_tenant_id_user_id_last_message_at_u~",
                table: "assistant_conversations",
                columns: new[] { "tenant_id", "user_id", "last_message_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_assistant_messages_tenant_id_conversation_id_id",
                table: "assistant_messages",
                columns: new[] { "tenant_id", "conversation_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_assistant_proposals_tenant_id_proposed_by_user_id",
                table: "assistant_proposals",
                columns: new[] { "tenant_id", "proposed_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_assistant_proposals_tenant_id_status_created_at_utc",
                table: "assistant_proposals",
                columns: new[] { "tenant_id", "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_assistant_reports_tenant_id_created_at_utc",
                table: "assistant_reports",
                columns: new[] { "tenant_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_assistant_ticket_counters_tenant_id",
                table: "assistant_ticket_counters",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assistant_tickets_tenant_id_number",
                table: "assistant_tickets",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assistant_tickets_tenant_id_status",
                table: "assistant_tickets",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_chunks_tenant_id_document_id_ordinal",
                table: "knowledge_chunks",
                columns: new[] { "tenant_id", "document_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_documents_tenant_id_content_hash",
                table: "knowledge_documents",
                columns: new[] { "tenant_id", "content_hash" },
                unique: true);

            // xmin (assistant_proposals) is a PostgreSQL system column used as the concurrency token; it is intentionally not created.

            foreach (var table in TenantTables)
            {
                RowLevelSecurity.EnableTenantIsolation(migrationBuilder, table);
            }

            // The audit log is append-only: even the table owner cannot rewrite history without disabling the trigger.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION assistant_audit_log_immutable() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'assistant_audit_log is append-only' USING ERRCODE = '42501';
                END
                $$;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER assistant_audit_log_append_only
                BEFORE UPDATE OR DELETE ON assistant_audit_log
                FOR EACH ROW EXECUTE FUNCTION assistant_audit_log_immutable();
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER assistant_audit_log_no_truncate
                BEFORE TRUNCATE ON assistant_audit_log
                FOR EACH STATEMENT EXECUTE FUNCTION assistant_audit_log_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS assistant_audit_log_no_truncate ON assistant_audit_log;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS assistant_audit_log_append_only ON assistant_audit_log;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS assistant_audit_log_immutable();");
            foreach (var table in TenantTables)
            {
                RowLevelSecurity.DisableTenantIsolation(migrationBuilder, table);
            }

            migrationBuilder.DropTable(
                name: "assistant_audit_log");

            migrationBuilder.DropTable(
                name: "assistant_messages");

            migrationBuilder.DropTable(
                name: "assistant_proposals");

            migrationBuilder.DropTable(
                name: "assistant_reports");

            migrationBuilder.DropTable(
                name: "assistant_ticket_counters");

            migrationBuilder.DropTable(
                name: "assistant_tickets");

            migrationBuilder.DropTable(
                name: "knowledge_chunks");

            migrationBuilder.DropTable(
                name: "assistant_conversations");

            migrationBuilder.DropTable(
                name: "knowledge_documents");
        }
    }
}
