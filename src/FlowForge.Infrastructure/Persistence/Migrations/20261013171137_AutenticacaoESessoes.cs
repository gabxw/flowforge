using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutenticacaoESessoes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "disabled",
                schema: "public",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "email",
                schema: "public",
                table: "users",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "failed_sign_ins",
                schema: "public",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "locked_until",
                schema: "public",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "normalized_email",
                schema: "public",
                table: "users",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "password_hash",
                schema: "public",
                table: "users",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "role",
                schema: "public",
                table: "users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Owner");

            migrationBuilder.CreateTable(
                name: "refresh_sessions",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_sessions", x => x.id);
                    table.CheckConstraint("ck_refresh_session_expiry", "expires_at > created_at");
                    table.ForeignKey(
                        name: "fk_refresh_sessions_user_id",
                        column: x => x.user_id,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.CheckConstraint("ck_refresh_token_hash", "octet_length(hash) = 32");
                    table.ForeignKey(
                        name: "fk_refresh_tokens_session_id",
                        column: x => x.session_id,
                        principalSchema: "public",
                        principalTable: "refresh_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_email",
                schema: "public",
                table: "users",
                column: "normalized_email",
                unique: true,
                filter: "normalized_email IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_auth_pair",
                schema: "public",
                table: "users",
                sql: "(email IS NULL AND normalized_email IS NULL AND password_hash IS NULL) OR (email IS NOT NULL AND normalized_email IS NOT NULL AND password_hash IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_failed_sign_ins",
                schema: "public",
                table: "users",
                sql: "failed_sign_ins BETWEEN 0 AND 5");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_role",
                schema: "public",
                table: "users",
                sql: "role IN ('Owner', 'Admin')");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_sessions_expires_at",
                schema: "public",
                table: "refresh_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_sessions_user_id_created_at",
                schema: "public",
                table: "refresh_sessions",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_hash",
                schema: "public",
                table: "refresh_tokens",
                column: "hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_session_id",
                schema: "public",
                table: "refresh_tokens",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refresh_tokens",
                schema: "public");

            migrationBuilder.DropTable(
                name: "refresh_sessions",
                schema: "public");

            migrationBuilder.DropIndex(
                name: "ix_users_normalized_email",
                schema: "public",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_auth_pair",
                schema: "public",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_failed_sign_ins",
                schema: "public",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_role",
                schema: "public",
                table: "users");

            migrationBuilder.DropColumn(
                name: "disabled",
                schema: "public",
                table: "users");

            migrationBuilder.DropColumn(
                name: "email",
                schema: "public",
                table: "users");

            migrationBuilder.DropColumn(
                name: "failed_sign_ins",
                schema: "public",
                table: "users");

            migrationBuilder.DropColumn(
                name: "locked_until",
                schema: "public",
                table: "users");

            migrationBuilder.DropColumn(
                name: "normalized_email",
                schema: "public",
                table: "users");

            migrationBuilder.DropColumn(
                name: "password_hash",
                schema: "public",
                table: "users");

            migrationBuilder.DropColumn(
                name: "role",
                schema: "public",
                table: "users");
        }
    }
}
