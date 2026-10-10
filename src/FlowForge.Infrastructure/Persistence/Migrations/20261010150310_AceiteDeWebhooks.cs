using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AceiteDeWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "trigger_input_protected",
                schema: "public",
                table: "workflow_executions",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "webhook_endpoint_id",
                schema: "public",
                table: "workflow_executions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_workflow_executions_id_workflow_id_owner_user_id",
                schema: "public",
                table: "workflow_executions",
                columns: new[] { "id", "workflow_id", "owner_user_id" });

            migrationBuilder.CreateTable(
                name: "webhook_endpoints",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rotated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_endpoints", x => x.id);
                    table.UniqueConstraint("ak_webhook_endpoints_id_owner_user_id", x => new { x.id, x.owner_user_id });
                    table.UniqueConstraint("ak_webhook_endpoints_id_workflow_id_owner_user_id", x => new { x.id, x.workflow_id, x.owner_user_id });
                    table.CheckConstraint("ck_webhook_endpoint", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND octet_length(secret_hash) = 32 AND (rotated_at IS NULL OR rotated_at >= created_at)");
                    table.ForeignKey(
                        name: "fk_webhook_endpoints_workflow_id_owner_user_id",
                        columns: x => new { x.workflow_id, x.owner_user_id },
                        principalSchema: "public",
                        principalTable: "workflows",
                        principalColumns: new[] { "id", "owner_user_id" });
                });

            migrationBuilder.CreateTable(
                name: "webhook_idempotency",
                schema: "public",
                columns: table => new
                {
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_idempotency", x => new { x.endpoint_id, x.key_digest });
                    table.CheckConstraint("ck_webhook_digests", "key_digest ~ '^[0-9A-F]{64}$' AND request_digest ~ '^[0-9A-F]{64}$'");
                    table.CheckConstraint("ck_webhook_retention", "expires_at > created_at");
                    table.ForeignKey(
                        name: "fk_webhook_idempotency_endpoint_id_workflow_id_owner_user_id",
                        columns: x => new { x.endpoint_id, x.workflow_id, x.owner_user_id },
                        principalSchema: "public",
                        principalTable: "webhook_endpoints",
                        principalColumns: new[] { "id", "workflow_id", "owner_user_id" });
                    table.ForeignKey(
                        name: "fk_webhook_idempotency_execution_id_workflow_id_owner_user_id",
                        columns: x => new { x.execution_id, x.workflow_id, x.owner_user_id },
                        principalSchema: "public",
                        principalTable: "workflow_executions",
                        principalColumns: new[] { "id", "workflow_id", "owner_user_id" });
                });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_executions_hook_id_workflow_id_owner_user_id",
                schema: "public",
                table: "workflow_executions",
                columns: new[] { "webhook_endpoint_id", "workflow_id", "owner_user_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_trigger_input",
                schema: "public",
                table: "workflow_executions",
                sql: "(webhook_endpoint_id IS NULL) = (trigger_input_protected IS NULL) AND (trigger_input_protected IS NULL OR octet_length(trigger_input_protected) BETWEEN 1 AND 131072)");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_endpoints_workflow_id",
                schema: "public",
                table: "webhook_endpoints",
                column: "workflow_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_endpoints_workflow_id_owner_user_id",
                schema: "public",
                table: "webhook_endpoints",
                columns: new[] { "workflow_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_idempotency_endpoint_id_workflow_id_owner_user_id",
                schema: "public",
                table: "webhook_idempotency",
                columns: new[] { "endpoint_id", "workflow_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_idempotency_execution_id_workflow_id_owner_user_id",
                schema: "public",
                table: "webhook_idempotency",
                columns: new[] { "execution_id", "workflow_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_idempotency_expires_at",
                schema: "public",
                table: "webhook_idempotency",
                column: "expires_at");

            migrationBuilder.AddForeignKey(
                name: "fk_workflow_executions_hook_id_workflow_id_owner_user_id",
                schema: "public",
                table: "workflow_executions",
                columns: new[] { "webhook_endpoint_id", "workflow_id", "owner_user_id" },
                principalSchema: "public",
                principalTable: "webhook_endpoints",
                principalColumns: new[] { "id", "workflow_id", "owner_user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_workflow_executions_hook_id_workflow_id_owner_user_id",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropTable(
                name: "webhook_idempotency",
                schema: "public");

            migrationBuilder.DropTable(
                name: "webhook_endpoints",
                schema: "public");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_workflow_executions_id_workflow_id_owner_user_id",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropIndex(
                name: "ix_workflow_executions_hook_id_workflow_id_owner_user_id",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_execution_trigger_input",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "trigger_input_protected",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "webhook_endpoint_id",
                schema: "public",
                table: "workflow_executions");
        }
    }
}
