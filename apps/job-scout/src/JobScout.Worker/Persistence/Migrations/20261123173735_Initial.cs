using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobScout.Worker.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "job_scout");

            migrationBuilder.CreateTable(
                name: "sources",
                schema: "job_scout",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    health = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sources", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                schema: "job_scout",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    availability = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    first_seen_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    current_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.id);
                    table.ForeignKey(
                        name: "FK_jobs_sources_source_id",
                        column: x => x.source_id,
                        principalSchema: "job_scout",
                        principalTable: "sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "source_scans",
                schema: "job_scout",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    search_profile_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    observed_count = table.Column<int>(type: "integer", nullable: false),
                    created_count = table.Column<int>(type: "integer", nullable: false),
                    updated_count = table.Column<int>(type: "integer", nullable: false),
                    unchanged_count = table.Column<int>(type: "integer", nullable: false),
                    evaluations_queued_count = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_scans", x => x.id);
                    table.ForeignKey(
                        name: "FK_source_scans_sources_source_id",
                        column: x => x.source_id,
                        principalSchema: "job_scout",
                        principalTable: "sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "job_revisions",
                schema: "job_scout",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_revisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_job_revisions_jobs_job_id",
                        column: x => x.job_id,
                        principalSchema: "job_scout",
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "evaluation_outbox",
                schema: "job_scout",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evaluation_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    dispatched_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evaluation_outbox", x => x.id);
                    table.ForeignKey(
                        name: "FK_evaluation_outbox_job_revisions_job_revision_id",
                        column: x => x.job_revision_id,
                        principalSchema: "job_scout",
                        principalTable: "job_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_evaluation_outbox_job_revision_id",
                schema: "job_scout",
                table: "evaluation_outbox",
                column: "job_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_evaluation_outbox_pending",
                schema: "job_scout",
                table: "evaluation_outbox",
                columns: new[] { "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_evaluation_outbox_key",
                schema: "job_scout",
                table: "evaluation_outbox",
                column: "evaluation_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_job_revisions_job_fingerprint",
                schema: "job_scout",
                table: "job_revisions",
                columns: new[] { "job_id", "fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_jobs_last_seen",
                schema: "job_scout",
                table: "jobs",
                column: "last_seen_at_utc");

            migrationBuilder.CreateIndex(
                name: "ux_jobs_source_external",
                schema: "job_scout",
                table: "jobs",
                columns: new[] { "source_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_source_scans_source_profile_started",
                schema: "job_scout",
                table: "source_scans",
                columns: new[] { "source_id", "search_profile_id", "started_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evaluation_outbox",
                schema: "job_scout");

            migrationBuilder.DropTable(
                name: "source_scans",
                schema: "job_scout");

            migrationBuilder.DropTable(
                name: "job_revisions",
                schema: "job_scout");

            migrationBuilder.DropTable(
                name: "jobs",
                schema: "job_scout");

            migrationBuilder.DropTable(
                name: "sources",
                schema: "job_scout");
        }
    }
}
