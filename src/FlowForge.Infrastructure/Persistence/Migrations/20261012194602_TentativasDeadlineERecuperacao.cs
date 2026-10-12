using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TentativasDeadlineERecuperacao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_execution_state",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_node_credential_revision",
                schema: "public",
                table: "node_executions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_node_execution_state",
                schema: "public",
                table: "node_executions");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deadline_at",
                schema: "public",
                table: "workflow_executions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "clock_timestamp() + interval '48 hours'");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "recovery_not_before",
                schema: "public",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "retry_at",
                schema: "public",
                table: "node_executions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "execution_redrives",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_publish_attempts = table.Column<int>(type: "integer", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_execution_redrives", x => x.id);
                    table.CheckConstraint("ck_redrive", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND expected_publish_attempts >= 0");
                    table.ForeignKey(
                        name: "fk_execution_redrives_message_id_execution_id",
                        columns: x => new { x.message_id, x.execution_id },
                        principalSchema: "public",
                        principalTable: "outbox_messages",
                        principalColumns: new[] { "id", "execution_id" });
                });

            migrationBuilder.CreateTable(
                name: "execution_replays",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    new_execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_digest = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_execution_replays", x => x.id);
                    table.CheckConstraint("ck_replay", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND original_execution_id <> new_execution_id AND key_digest ~ '^[0-9A-F]{64}$'");
                    table.ForeignKey(
                        name: "fk_execution_replays_new_execution_id_workflow_id_owner_user_id",
                        columns: x => new { x.new_execution_id, x.workflow_id, x.owner_user_id },
                        principalSchema: "public",
                        principalTable: "workflow_executions",
                        principalColumns: new[] { "id", "workflow_id", "owner_user_id" });
                    table.ForeignKey(
                        name: "fk_execution_replays_original_id_workflow_id_owner_user_id",
                        columns: x => new { x.original_execution_id, x.workflow_id, x.owner_user_id },
                        principalSchema: "public",
                        principalTable: "workflow_executions",
                        principalColumns: new[] { "id", "workflow_id", "owner_user_id" });
                });

            migrationBuilder.CreateTable(
                name: "node_execution_attempts",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<int>(type: "integer", nullable: true),
                    external_outcome = table.Column<int>(type: "integer", nullable: false),
                    dispatched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    http_status_code = table.Column<int>(type: "integer", nullable: true),
                    credential_revision_used = table.Column<int>(type: "integer", nullable: true),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    legacy = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_execution_attempts", x => x.id);
                    table.CheckConstraint("ck_attempt_identity", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND number > 0");
                    table.CheckConstraint("ck_attempt_metadata", "external_outcome BETWEEN 1 AND 5 AND (http_status_code IS NULL OR http_status_code BETWEEN 100 AND 599) AND (credential_revision_used IS NULL OR credential_revision_used > 0) AND (dispatched_at IS NULL OR (dispatched_at >= started_at AND (finished_at IS NULL OR dispatched_at <= finished_at))) AND (retry_at IS NULL OR (finished_at IS NOT NULL AND retry_at > finished_at))");
                    table.CheckConstraint("ck_attempt_state", "(status = 1 AND finished_at IS NULL AND error_code IS NULL) OR (status IN (2, 4) AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NULL) OR (status IN (3, 5) AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 20)");
                    table.ForeignKey(
                        name: "fk_node_execution_attempts_node_execution_id_execution_id",
                        columns: x => new { x.node_execution_id, x.execution_id },
                        principalSchema: "public",
                        principalTable: "node_executions",
                        principalColumns: new[] { "id", "execution_id" });
                });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_executions_deadline_at_id",
                schema: "public",
                table: "workflow_executions",
                columns: new[] { "deadline_at", "id" },
                filter: "status IN (1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_deadline",
                schema: "public",
                table: "workflow_executions",
                sql: "deadline_at > created_at");

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_state",
                schema: "public",
                table: "workflow_executions",
                sql: "(status = 1 AND started_at IS NULL AND finished_at IS NULL AND error_code IS NULL) OR (status = 2 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NULL AND error_code IS NULL) OR (status = 4 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 20) OR (status = 3 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NULL) OR (status = 5 AND cancel_requested_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= cancel_requested_at AND finished_at >= COALESCE(started_at, created_at) AND (started_at IS NULL OR started_at >= created_at) AND error_code IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_node_credential_revision",
                schema: "public",
                table: "node_executions",
                sql: "credential_revision_used IS NULL OR (credential_revision_used > 0 AND attempt_count > 0 AND status IN (2, 3, 4, 5, 7))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_node_execution_state",
                schema: "public",
                table: "node_executions",
                sql: "(status = 1 AND attempt_count = 0 AND started_at IS NULL AND finished_at IS NULL AND input_summary IS NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 2 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NULL AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 3 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NOT NULL AND error_code IS NULL) OR (status = 4 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 20) OR (status = 5 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NULL AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 20 AND retry_at > started_at) OR (status = 6 AND attempt_count = 0 AND started_at IS NULL AND finished_at IS NOT NULL AND input_summary IS NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 7 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_node_retry",
                schema: "public",
                table: "node_executions",
                sql: "(status = 5) = (retry_at IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_claim_until_execution_id",
                schema: "public",
                table: "inbox_messages",
                columns: new[] { "claim_until", "execution_id" },
                filter: "completed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_execution_redrives_execution_id_requested_at",
                schema: "public",
                table: "execution_redrives",
                columns: new[] { "execution_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_execution_redrives_message_id_execution_id",
                schema: "public",
                table: "execution_redrives",
                columns: new[] { "message_id", "execution_id" });

            migrationBuilder.CreateIndex(
                name: "ix_execution_redrives_message_id_expected_publish_attempts",
                schema: "public",
                table: "execution_redrives",
                columns: new[] { "message_id", "expected_publish_attempts" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_execution_replays_new_execution_id",
                schema: "public",
                table: "execution_replays",
                column: "new_execution_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_execution_replays_new_execution_id_workflow_id_owner_user_id",
                schema: "public",
                table: "execution_replays",
                columns: new[] { "new_execution_id", "workflow_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_execution_replays_original_id_key_digest",
                schema: "public",
                table: "execution_replays",
                columns: new[] { "original_execution_id", "key_digest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_execution_replays_original_id_workflow_id_owner_user_id",
                schema: "public",
                table: "execution_replays",
                columns: new[] { "original_execution_id", "workflow_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_node_execution_attempts_execution_id_started_at_id",
                schema: "public",
                table: "node_execution_attempts",
                columns: new[] { "execution_id", "started_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_node_execution_attempts_node_execution_id_execution_id",
                schema: "public",
                table: "node_execution_attempts",
                columns: new[] { "node_execution_id", "execution_id" });

            migrationBuilder.CreateIndex(
                name: "ix_node_execution_attempts_node_execution_id_number",
                schema: "public",
                table: "node_execution_attempts",
                columns: new[] { "node_execution_id", "number" },
                unique: true);

            // Histórico anterior não possui horários por tentativa. Importa somente o resumo conhecido, marcado Legacy.
            migrationBuilder.Sql("""
                INSERT INTO node_execution_attempts
                    (id, execution_id, node_execution_id, number, status, started_at, finished_at, error_code,
                     external_outcome, dispatched_at, http_status_code, credential_revision_used, retry_at, legacy)
                SELECT md5(n.id::text || ':legacy')::uuid, n.execution_id, n.id, n.attempt_count,
                       CASE n.status WHEN 2 THEN 1 WHEN 3 THEN 2 WHEN 4 THEN 3 WHEN 7 THEN 4 END,
                       n.started_at, n.finished_at, n.error_code,
                       CASE WHEN d.type = 2 THEN CASE WHEN n.status = 3 THEN 3 ELSE 5 END ELSE 1 END,
                       NULL, NULL, n.credential_revision_used, NULL, true
                FROM node_executions n JOIN workflow_nodes d
                  ON d.workflow_version_id = n.workflow_version_id AND d.node_id = n.node_id
                WHERE n.attempt_count > 0 AND n.status IN (2, 3, 4, 7);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "execution_redrives",
                schema: "public");

            migrationBuilder.DropTable(
                name: "execution_replays",
                schema: "public");

            migrationBuilder.DropTable(
                name: "node_execution_attempts",
                schema: "public");

            migrationBuilder.DropIndex(
                name: "ix_workflow_executions_deadline_at_id",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_execution_deadline",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_execution_state",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_node_credential_revision",
                schema: "public",
                table: "node_executions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_node_execution_state",
                schema: "public",
                table: "node_executions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_node_retry",
                schema: "public",
                table: "node_executions");

            migrationBuilder.DropIndex(
                name: "ix_inbox_messages_claim_until_execution_id",
                schema: "public",
                table: "inbox_messages");

            migrationBuilder.DropColumn(
                name: "deadline_at",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "recovery_not_before",
                schema: "public",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "retry_at",
                schema: "public",
                table: "node_executions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_state",
                schema: "public",
                table: "workflow_executions",
                sql: "(status = 1 AND started_at IS NULL AND finished_at IS NULL AND error_code IS NULL) OR (status = 2 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NULL AND error_code IS NULL) OR (status = 4 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 18) OR (status = 3 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NULL) OR (status = 5 AND cancel_requested_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= cancel_requested_at AND finished_at >= COALESCE(started_at, created_at) AND (started_at IS NULL OR started_at >= created_at) AND error_code IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_node_credential_revision",
                schema: "public",
                table: "node_executions",
                sql: "credential_revision_used IS NULL OR (credential_revision_used > 0 AND attempt_count > 0 AND status IN (2, 3, 4, 7))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_node_execution_state",
                schema: "public",
                table: "node_executions",
                sql: "(status = 1 AND attempt_count = 0 AND started_at IS NULL AND finished_at IS NULL AND input_summary IS NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 2 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NULL AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 3 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NOT NULL AND error_code IS NULL) OR (status = 4 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 18) OR (status = 6 AND attempt_count = 0 AND started_at IS NULL AND finished_at IS NOT NULL AND input_summary IS NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 7 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NULL)");
        }
    }
}
