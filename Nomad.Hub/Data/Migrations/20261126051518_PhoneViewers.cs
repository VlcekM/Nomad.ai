using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nomad.Hub.Data.Migrations;

/// <inheritdoc />
public partial class PhoneViewers : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "ForViewer",
            table: "EnrollTokens",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "IsViewer",
            table: "Devices",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ForViewer",
            table: "EnrollTokens");

        migrationBuilder.DropColumn(
            name: "IsViewer",
            table: "Devices");
    }
}
