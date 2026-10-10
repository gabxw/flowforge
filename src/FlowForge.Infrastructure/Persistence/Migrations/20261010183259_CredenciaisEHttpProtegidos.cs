using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CredenciaisEHttpProtegidos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_execution_state",
                schema: "public",
                table: "workflow_executions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_node_execution_state",
                schema: "public",
                table: "node_executions");

            migrationBuilder.AddColumn<int>(
                name: "credential_revision_used",
                schema: "public",
                table: "node_executions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "credentials",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    origin = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    header_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    protected_value = table.Column<byte[]>(type: "bytea", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credentials", x => x.id);
                    table.UniqueConstraint("ak_credentials_id_owner_user_id", x => new { x.id, x.owner_user_id });
                    table.CheckConstraint("ck_credential_identity", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND revision > 0");
                    table.CheckConstraint("ck_credential_time", "updated_at >= created_at AND (revoked_at IS NULL OR revoked_at = updated_at)");
                    table.CheckConstraint("ck_credential_type", "(type = 1 AND header_name IS NULL) OR (type = 2 AND header_name IS NOT NULL)");
                    table.CheckConstraint("ck_credential_value", "octet_length(protected_value) BETWEEN 1 AND 16384");
                    table.ForeignKey(
                        name: "fk_credentials_owner_user_id",
                        column: x => x.owner_user_id,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_nodes_credential_id_owner_user_id",
                schema: "public",
                table: "workflow_nodes",
                columns: new[] { "credential_id", "owner_user_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_state",
                schema: "public",
                table: "workflow_executions",
                sql: "(status = 1 AND started_at IS NULL AND finished_at IS NULL AND error_code IS NULL) OR (status = 2 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NULL AND error_code IS NULL) OR (status = 4 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 14) OR (status = 3 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NULL) OR (status = 5 AND cancel_requested_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= cancel_requested_at AND finished_at >= COALESCE(started_at, created_at) AND (started_at IS NULL OR started_at >= created_at) AND error_code IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_node_credential_revision",
                schema: "public",
                table: "node_executions",
                sql: "credential_revision_used IS NULL OR (credential_revision_used > 0 AND attempt_count > 0 AND status IN (2, 3, 4, 7))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_node_execution_state",
                schema: "public",
                table: "node_executions",
                sql: "(status = 1 AND attempt_count = 0 AND started_at IS NULL AND finished_at IS NULL AND input_summary IS NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 2 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NULL AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 3 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NOT NULL AND error_code IS NULL) OR (status = 4 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 14) OR (status = 6 AND attempt_count = 0 AND started_at IS NULL AND finished_at IS NOT NULL AND input_summary IS NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 7 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_credentials_owner_user_id_created_at_id",
                schema: "public",
                table: "credentials",
                columns: new[] { "owner_user_id", "created_at", "id" });

            migrationBuilder.AddForeignKey(
                name: "fk_workflow_nodes_credential_id_owner_user_id",
                schema: "public",
                table: "workflow_nodes",
                columns: new[] { "credential_id", "owner_user_id" },
                principalSchema: "public",
                principalTable: "credentials",
                principalColumns: new[] { "id", "owner_user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_workflow_nodes_credential_id_owner_user_id",
                schema: "public",
                table: "workflow_nodes");

            migrationBuilder.DropTable(
                name: "credentials",
                schema: "public");

            migrationBuilder.DropIndex(
                name: "ix_workflow_nodes_credential_id_owner_user_id",
                schema: "public",
                table: "workflow_nodes");

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

            migrationBuilder.DropColumn(
                name: "credential_revision_used",
                schema: "public",
                table: "node_executions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_state",
                schema: "public",
                table: "workflow_executions",
                sql: "(status = 1 AND started_at IS NULL AND finished_at IS NULL AND error_code IS NULL) OR (status = 2 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NULL AND error_code IS NULL) OR (status = 4 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 7) OR (status = 3 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NULL) OR (status = 5 AND cancel_requested_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= cancel_requested_at AND finished_at >= COALESCE(started_at, created_at) AND (started_at IS NULL OR started_at >= created_at) AND error_code IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_node_execution_state",
                schema: "public",
                table: "node_executions",
                sql: "(status = 1 AND attempt_count = 0 AND started_at IS NULL AND finished_at IS NULL AND input_summary IS NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 2 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NULL AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 3 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NOT NULL AND error_code IS NULL) OR (status = 4 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 7) OR (status = 6 AND attempt_count = 0 AND started_at IS NULL AND finished_at IS NOT NULL AND input_summary IS NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 7 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NULL)");
        }
    }
}
