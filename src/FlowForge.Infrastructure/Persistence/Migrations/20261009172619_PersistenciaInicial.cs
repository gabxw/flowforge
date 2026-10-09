using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistenciaInicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "public");

            migrationBuilder.CreateTable(
                name: "users",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                });

            migrationBuilder.CreateTable(
                name: "workflow_connections",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_port = table.Column<string>(type: "text", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_connections", x => x.id);
                    table.CheckConstraint("ck_workflow_connections_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_workflow_connections_ordinal", "ordinal >= 0 AND length(btrim(source_port)) > 0");
                });

            migrationBuilder.CreateTable(
                name: "workflow_nodes",
                schema: "public",
                columns: table => new
                {
                    workflow_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    configuration = table.Column<string>(type: "jsonb", nullable: false),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                    position_x = table.Column<double>(type: "double precision", nullable: false),
                    position_y = table.Column<double>(type: "double precision", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_nodes", x => new { x.workflow_version_id, x.node_id });
                    table.CheckConstraint("ck_workflow_nodes_config", "jsonb_typeof(configuration) = 'object' AND configuration ? 'schemaVersion' AND configuration->>'schemaVersion' = '1'");
                    table.CheckConstraint("ck_workflow_nodes_credential", "credential_id IS NULL OR (type = 2 AND credential_id <> '00000000-0000-0000-0000-000000000000'::uuid)");
                    table.CheckConstraint("ck_workflow_nodes_id", "node_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_workflow_nodes_position", "position_x > '-Infinity'::float8 AND position_x < 'Infinity'::float8 AND position_y > '-Infinity'::float8 AND position_y < 'Infinity'::float8");
                    table.CheckConstraint("ck_workflow_nodes_type", "type BETWEEN 1 AND 6 AND ordinal >= 0");
                });

            migrationBuilder.CreateTable(
                name: "workflow_versions",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_versions", x => x.id);
                    table.UniqueConstraint("ak_workflow_versions_id_workflow_id_owner_user_id", x => new { x.id, x.workflow_id, x.owner_user_id });
                    table.CheckConstraint("ck_workflow_versions_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_workflow_versions_revision", "revision >= 0 AND version_number > 0");
                    table.CheckConstraint("ck_workflow_versions_status", "(status = 1 AND published_at IS NULL) OR (status = 2 AND published_at IS NOT NULL AND published_at >= created_at)");
                });

            migrationBuilder.CreateTable(
                name: "workflows",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    current_published_version_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflows", x => x.id);
                    table.UniqueConstraint("ak_workflows_id_owner_user_id", x => new { x.id, x.owner_user_id });
                    table.CheckConstraint("ck_workflows_dates", "updated_at >= created_at AND (archived_at IS NULL OR archived_at = updated_at)");
                    table.CheckConstraint("ck_workflows_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_workflows_name", "length(btrim(name)) BETWEEN 1 AND 200");
                    table.CheckConstraint("ck_workflows_revision", "revision >= 0");
                    table.ForeignKey(
                        name: "fk_workflows_current_published_version_id_id_owner_user_id",
                        columns: x => new { x.current_published_version_id, x.id, x.owner_user_id },
                        principalSchema: "public",
                        principalTable: "workflow_versions",
                        principalColumns: new[] { "id", "workflow_id", "owner_user_id" });
                    table.ForeignKey(
                        name: "fk_workflows_owner_user_id",
                        column: x => x.owner_user_id,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_connections_version_id_ordinal",
                schema: "public",
                table: "workflow_connections",
                columns: new[] { "workflow_version_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_connections_version_id_source_node_id_source_port",
                schema: "public",
                table: "workflow_connections",
                columns: new[] { "workflow_version_id", "source_node_id", "source_port" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_connections_version_id_target_node_id",
                schema: "public",
                table: "workflow_connections",
                columns: new[] { "workflow_version_id", "target_node_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_nodes_version_id_ordinal",
                schema: "public",
                table: "workflow_nodes",
                columns: new[] { "workflow_version_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_nodes_version_id_workflow_id_owner_user_id",
                schema: "public",
                table: "workflow_nodes",
                columns: new[] { "workflow_version_id", "workflow_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_versions_workflow_id",
                schema: "public",
                table: "workflow_versions",
                column: "workflow_id",
                unique: true,
                filter: "status = 1");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_versions_workflow_id_owner_user_id",
                schema: "public",
                table: "workflow_versions",
                columns: new[] { "workflow_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_versions_workflow_id_version_number",
                schema: "public",
                table: "workflow_versions",
                columns: new[] { "workflow_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflows_current_published_version_id_id_owner_user_id",
                schema: "public",
                table: "workflows",
                columns: new[] { "current_published_version_id", "id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workflows_owner_user_id_updated_at_id",
                schema: "public",
                table: "workflows",
                columns: new[] { "owner_user_id", "updated_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.AddForeignKey(
                name: "fk_workflow_connections_workflow_version_id_source_node_id",
                schema: "public",
                table: "workflow_connections",
                columns: new[] { "workflow_version_id", "source_node_id" },
                principalSchema: "public",
                principalTable: "workflow_nodes",
                principalColumns: new[] { "workflow_version_id", "node_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_workflow_connections_workflow_version_id_target_node_id",
                schema: "public",
                table: "workflow_connections",
                columns: new[] { "workflow_version_id", "target_node_id" },
                principalSchema: "public",
                principalTable: "workflow_nodes",
                principalColumns: new[] { "workflow_version_id", "node_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_workflow_nodes_workflow_version_id_workflow_id_owner_user_id",
                schema: "public",
                table: "workflow_nodes",
                columns: new[] { "workflow_version_id", "workflow_id", "owner_user_id" },
                principalSchema: "public",
                principalTable: "workflow_versions",
                principalColumns: new[] { "id", "workflow_id", "owner_user_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_workflow_versions_workflow_id_owner_user_id",
                schema: "public",
                table: "workflow_versions",
                columns: new[] { "workflow_id", "owner_user_id" },
                principalSchema: "public",
                principalTable: "workflows",
                principalColumns: new[] { "id", "owner_user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_workflows_current_published_version_id_id_owner_user_id",
                schema: "public",
                table: "workflows");

            migrationBuilder.DropTable(
                name: "workflow_connections",
                schema: "public");

            migrationBuilder.DropTable(
                name: "workflow_nodes",
                schema: "public");

            migrationBuilder.DropTable(
                name: "workflow_versions",
                schema: "public");

            migrationBuilder.DropTable(
                name: "workflows",
                schema: "public");

            migrationBuilder.DropTable(
                name: "users",
                schema: "public");
        }
    }
}
