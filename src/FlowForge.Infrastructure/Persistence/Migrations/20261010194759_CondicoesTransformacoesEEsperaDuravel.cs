using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CondicoesTransformacoesEEsperaDuravel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_execution_state",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_execution_id",
                schema: "public",
                table: "outbox_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_outbox_contract",
                schema: "public",
                table: "outbox_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_node_execution_state",
                schema: "public",
                table: "node_executions");

            migrationBuilder.DropIndex(
                name: "ix_inbox_messages_execution_id",
                schema: "public",
                table: "inbox_messages");

            migrationBuilder.AddColumn<int>(
                name: "dispatch_sequence",
                schema: "public",
                table: "workflow_executions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "resume_at",
                schema: "public",
                table: "workflow_executions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "dispatch_sequence",
                schema: "public",
                table: "outbox_messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_resume",
                schema: "public",
                table: "workflow_executions",
                sql: "dispatch_sequence >= 0 AND (resume_at IS NULL OR (status = 2 AND next_node_id IS NOT NULL AND resume_at > started_at))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_state",
                schema: "public",
                table: "workflow_executions",
                sql: "(status = 1 AND started_at IS NULL AND finished_at IS NULL AND error_code IS NULL) OR (status = 2 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NULL AND error_code IS NULL) OR (status = 4 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 18) OR (status = 3 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NULL) OR (status = 5 AND cancel_requested_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= cancel_requested_at AND finished_at >= COALESCE(started_at, created_at) AND (started_at IS NULL OR started_at >= created_at) AND error_code IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_execution_id_dispatch_sequence",
                schema: "public",
                table: "outbox_messages",
                columns: new[] { "execution_id", "dispatch_sequence" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_outbox_contract",
                schema: "public",
                table: "outbox_messages",
                sql: "contract_version = 1 AND publish_attempts >= 0 AND dispatch_sequence >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_node_execution_state",
                schema: "public",
                table: "node_executions",
                sql: "(status = 1 AND attempt_count = 0 AND started_at IS NULL AND finished_at IS NULL AND input_summary IS NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 2 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NULL AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 3 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NOT NULL AND error_code IS NULL) OR (status = 4 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 18) OR (status = 6 AND attempt_count = 0 AND started_at IS NULL AND finished_at IS NOT NULL AND input_summary IS NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 7 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_execution_id",
                schema: "public",
                table: "inbox_messages",
                column: "execution_id",
                unique: true,
                filter: "completed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_execution_resume",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_execution_state",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_execution_id_dispatch_sequence",
                schema: "public",
                table: "outbox_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_outbox_contract",
                schema: "public",
                table: "outbox_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_node_execution_state",
                schema: "public",
                table: "node_executions");

            migrationBuilder.DropIndex(
                name: "ix_inbox_messages_execution_id",
                schema: "public",
                table: "inbox_messages");

            migrationBuilder.DropColumn(
                name: "dispatch_sequence",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "resume_at",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "dispatch_sequence",
                schema: "public",
                table: "outbox_messages");

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_state",
                schema: "public",
                table: "workflow_executions",
                sql: "(status = 1 AND started_at IS NULL AND finished_at IS NULL AND error_code IS NULL) OR (status = 2 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NULL AND error_code IS NULL) OR (status = 4 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 14) OR (status = 3 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NULL) OR (status = 5 AND cancel_requested_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= cancel_requested_at AND finished_at >= COALESCE(started_at, created_at) AND (started_at IS NULL OR started_at >= created_at) AND error_code IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_execution_id",
                schema: "public",
                table: "outbox_messages",
                column: "execution_id",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_outbox_contract",
                schema: "public",
                table: "outbox_messages",
                sql: "contract_version = 1 AND publish_attempts >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_node_execution_state",
                schema: "public",
                table: "node_executions",
                sql: "(status = 1 AND attempt_count = 0 AND started_at IS NULL AND finished_at IS NULL AND input_summary IS NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 2 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NULL AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 3 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NOT NULL AND error_code IS NULL) OR (status = 4 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 14) OR (status = 6 AND attempt_count = 0 AND started_at IS NULL AND finished_at IS NOT NULL AND input_summary IS NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 7 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_execution_id",
                schema: "public",
                table: "inbox_messages",
                column: "execution_id",
                unique: true);
        }
    }
}
