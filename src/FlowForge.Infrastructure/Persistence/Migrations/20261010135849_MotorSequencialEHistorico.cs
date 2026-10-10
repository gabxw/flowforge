using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MotorSequencialEHistorico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_execution_state",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancel_requested_at",
                schema: "public",
                table: "workflow_executions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "checkpoint_revision",
                schema: "public",
                table: "workflow_executions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte[]>(
                name: "execution_context_protected",
                schema: "public",
                table: "workflow_executions",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "next_node_id",
                schema: "public",
                table: "workflow_executions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_workflow_executions_id_workflow_version_id",
                schema: "public",
                table: "workflow_executions",
                columns: new[] { "id", "workflow_version_id" });

            migrationBuilder.CreateTable(
                name: "node_executions",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    input_summary = table.Column<string>(type: "jsonb", nullable: true),
                    output_summary = table.Column<string>(type: "jsonb", nullable: true),
                    error_code = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_executions", x => x.id);
                    table.UniqueConstraint("ak_node_executions_id_execution_id", x => new { x.id, x.execution_id });
                    table.CheckConstraint("ck_node_execution_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND ordinal >= 0");
                    table.CheckConstraint("ck_node_execution_state", "(status = 1 AND attempt_count = 0 AND started_at IS NULL AND finished_at IS NULL AND input_summary IS NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 2 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NULL AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 3 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NOT NULL AND error_code IS NULL) OR (status = 4 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 7) OR (status = 6 AND attempt_count = 0 AND started_at IS NULL AND finished_at IS NOT NULL AND input_summary IS NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 7 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NULL)");
                    table.ForeignKey(
                        name: "fk_node_executions_execution_id_workflow_version_id",
                        columns: x => new { x.execution_id, x.workflow_version_id },
                        principalSchema: "public",
                        principalTable: "workflow_executions",
                        principalColumns: new[] { "id", "workflow_version_id" });
                    table.ForeignKey(
                        name: "fk_node_executions_workflow_version_id_node_id",
                        columns: x => new { x.workflow_version_id, x.node_id },
                        principalSchema: "public",
                        principalTable: "workflow_nodes",
                        principalColumns: new[] { "workflow_version_id", "node_id" });
                });

            migrationBuilder.CreateTable(
                name: "execution_logs",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    event_code = table.Column<int>(type: "integer", nullable: false),
                    message_protected = table.Column<byte[]>(type: "bytea", nullable: false),
                    message_byte_length = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_execution_logs", x => x.id);
                    table.CheckConstraint("ck_execution_log_content", "event_code = 1 AND message_byte_length BETWEEN 1 AND 8000 AND octet_length(message_protected) BETWEEN 1 AND 16384");
                    table.ForeignKey(
                        name: "fk_execution_logs_node_execution_id_execution_id",
                        columns: x => new { x.node_execution_id, x.execution_id },
                        principalSchema: "public",
                        principalTable: "node_executions",
                        principalColumns: new[] { "id", "execution_id" });
                });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_executions_version_id_next_node_id",
                schema: "public",
                table: "workflow_executions",
                columns: new[] { "workflow_version_id", "next_node_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_checkpoint",
                schema: "public",
                table: "workflow_executions",
                sql: "checkpoint_revision >= 0 AND (execution_context_protected IS NULL OR octet_length(execution_context_protected) BETWEEN 1 AND 131072) AND (cancel_requested_at IS NULL OR cancel_requested_at >= created_at) AND (status IN (1, 2) OR next_node_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_state",
                schema: "public",
                table: "workflow_executions",
                sql: "(status = 1 AND started_at IS NULL AND finished_at IS NULL AND error_code IS NULL) OR (status = 2 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NULL AND error_code IS NULL) OR (status = 4 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 7) OR (status = 3 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NULL) OR (status = 5 AND cancel_requested_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= cancel_requested_at AND finished_at >= COALESCE(started_at, created_at) AND (started_at IS NULL OR started_at >= created_at) AND error_code IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_execution_logs_execution_id_created_at_id",
                schema: "public",
                table: "execution_logs",
                columns: new[] { "execution_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_execution_logs_node_execution_id",
                schema: "public",
                table: "execution_logs",
                column: "node_execution_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_execution_logs_node_execution_id_execution_id",
                schema: "public",
                table: "execution_logs",
                columns: new[] { "node_execution_id", "execution_id" });

            migrationBuilder.CreateIndex(
                name: "ix_node_executions_execution_id_node_id",
                schema: "public",
                table: "node_executions",
                columns: new[] { "execution_id", "node_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_node_executions_execution_id_ordinal",
                schema: "public",
                table: "node_executions",
                columns: new[] { "execution_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_node_executions_execution_id_version_id",
                schema: "public",
                table: "node_executions",
                columns: new[] { "execution_id", "workflow_version_id" });

            migrationBuilder.CreateIndex(
                name: "ix_node_executions_version_id_node_id",
                schema: "public",
                table: "node_executions",
                columns: new[] { "workflow_version_id", "node_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_workflow_executions_workflow_version_id_next_node_id",
                schema: "public",
                table: "workflow_executions",
                columns: new[] { "workflow_version_id", "next_node_id" },
                principalSchema: "public",
                principalTable: "workflow_nodes",
                principalColumns: new[] { "workflow_version_id", "node_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_workflow_executions_workflow_version_id_next_node_id",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropTable(
                name: "execution_logs",
                schema: "public");

            migrationBuilder.DropTable(
                name: "node_executions",
                schema: "public");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_workflow_executions_id_workflow_version_id",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropIndex(
                name: "ix_workflow_executions_version_id_next_node_id",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_execution_checkpoint",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_execution_state",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "cancel_requested_at",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "checkpoint_revision",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "execution_context_protected",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "next_node_id",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_state",
                schema: "public",
                table: "workflow_executions",
                sql: "(status = 1 AND started_at IS NULL AND finished_at IS NULL AND error_code IS NULL) OR (status = 2 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NULL AND error_code IS NULL) OR (status = 4 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NOT NULL AND error_code = 1)");
        }
    }
}
