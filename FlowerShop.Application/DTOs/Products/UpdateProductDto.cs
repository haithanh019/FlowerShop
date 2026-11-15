namespace FlowerShop.Application.DTOs.Products;

public sealed class UpdateProductDto : ProductUpsertDto
{
    public bool IsActive { get; set; }
}
