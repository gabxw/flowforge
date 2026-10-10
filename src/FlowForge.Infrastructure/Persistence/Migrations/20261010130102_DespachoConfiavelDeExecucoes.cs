using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DespachoConfiavelDeExecucoes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workflow_executions",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_executions", x => x.id);
                    table.CheckConstraint("ck_execution_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND correlation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_execution_state", "(status = 1 AND started_at IS NULL AND finished_at IS NULL AND error_code IS NULL) OR (status = 2 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NULL AND error_code IS NULL) OR (status = 4 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NOT NULL AND error_code = 1)");
                    table.ForeignKey(
                        name: "fk_workflow_executions_version_id_workflow_id_owner_user_id",
                        columns: x => new { x.workflow_version_id, x.workflow_id, x.owner_user_id },
                        principalSchema: "public",
                        principalTable: "workflow_versions",
                        principalColumns: new[] { "id", "workflow_id", "owner_user_id" });
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    available_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claim_token = table.Column<Guid>(type: "uuid", nullable: true),
                    claim_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    publish_attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                    table.UniqueConstraint("ak_outbox_messages_id_execution_id", x => new { x.id, x.execution_id });
                    table.CheckConstraint("ck_outbox_contract", "contract_version = 1 AND publish_attempts >= 0");
                    table.CheckConstraint("ck_outbox_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND correlation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_outbox_lease", "(claim_token IS NULL) = (claim_until IS NULL) AND (published_at IS NULL OR (claim_token IS NULL AND published_at >= created_at))");
                    table.ForeignKey(
                        name: "fk_outbox_messages_execution_id",
                        column: x => x.execution_id,
                        principalSchema: "public",
                        principalTable: "workflow_executions",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "public",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claim_token = table.Column<Guid>(type: "uuid", nullable: true),
                    claim_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claim_attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_messages", x => x.message_id);
                    table.CheckConstraint("ck_inbox_state", "claim_attempts > 0 AND ((completed_at IS NULL AND claim_token IS NOT NULL AND claim_until IS NOT NULL) OR (completed_at IS NOT NULL AND completed_at >= received_at AND claim_token IS NULL AND claim_until IS NULL))");
                    table.ForeignKey(
                        name: "fk_inbox_messages_message_id_execution_id",
                        columns: x => new { x.message_id, x.execution_id },
                        principalSchema: "public",
                        principalTable: "outbox_messages",
                        principalColumns: new[] { "id", "execution_id" });
                });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_execution_id",
                schema: "public",
                table: "inbox_messages",
                column: "execution_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_message_id_execution_id",
                schema: "public",
                table: "inbox_messages",
                columns: new[] { "message_id", "execution_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_available_at_id",
                schema: "public",
                table: "outbox_messages",
                columns: new[] { "available_at", "id" },
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_execution_id",
                schema: "public",
                table: "outbox_messages",
                column: "execution_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_executions_owner_user_id_created_at_id",
                schema: "public",
                table: "workflow_executions",
                columns: new[] { "owner_user_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_executions_version_id_workflow_id_owner_user_id",
                schema: "public",
                table: "workflow_executions",
                columns: new[] { "workflow_version_id", "workflow_id", "owner_user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "public");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "public");

            migrationBuilder.DropTable(
                name: "workflow_executions",
                schema: "public");
        }
    }
}
