using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowerShop.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVoucherUsageCompositeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Orders_VoucherId_UserId_Status",
                table: "Orders",
                columns: new[] { "VoucherId", "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_VoucherId_UserId_Status",
                table: "Orders");
        }
    }
}
