using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamProctoring.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MakeQuestionMarksNullable : Migration
    {
        /// <inheritdoc />
        // Owner decision 2026-10-25: a question of a Draft exam may have no marks yet. Existing rows keep their values,
        // and CK_Questions_Marks ([Marks] > 0) stays: SQL Server accepts NULL there and still rejects 0 and below.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The check depends on the column, so it is dropped around the change and added back with the same rule.
            migrationBuilder.DropCheckConstraint(name: "CK_Questions_Marks", table: "Questions");

            migrationBuilder.AlterColumn<decimal>(
                name: "Marks",
                table: "Questions",
                type: "decimal(6,2)",
                precision: 6,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(6,2)",
                oldPrecision: 6,
                oldScale: 2);

            migrationBuilder.AddCheckConstraint(name: "CK_Questions_Marks", table: "Questions", sql: "[Marks] > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No default is written into questions without marks (0 would also break CK_Questions_Marks): rolling back
            // stops until every question has marks.
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM [Questions] WHERE [Marks] IS NULL) " +
                "THROW 50000, 'Some questions have no marks. Set their marks before rolling back MakeQuestionMarksNullable.', 1;");

            migrationBuilder.DropCheckConstraint(name: "CK_Questions_Marks", table: "Questions");

            migrationBuilder.AlterColumn<decimal>(
                name: "Marks",
                table: "Questions",
                type: "decimal(6,2)",
                precision: 6,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(6,2)",
                oldPrecision: 6,
                oldScale: 2,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(name: "CK_Questions_Marks", table: "Questions", sql: "[Marks] > 0");
        }
    }
}
