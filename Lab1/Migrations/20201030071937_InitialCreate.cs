using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Lab1.Migrations
{
    public partial class InitialCreate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                "Caches",
                table => new
                {
                    CacheId = table.Column<string>("TEXT", maxLength: 128, nullable: false),
                    CachedTime = table.Column<DateTime>("TEXT", nullable: false),
                    ExpireTime = table.Column<DateTime>("TEXT", nullable: false),
                    Content = table.Column<byte[]>("BLOB", maxLength: 40960, nullable: true)
                },
                constraints: table => { table.PrimaryKey("PK_Caches", x => x.CacheId); });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                "Caches");
        }
    }
}