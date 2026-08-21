namespace Scan2EnterGateway.Models;

public sealed class PriceListDto
{
    public int PriceListId { get; set; }
    public string Name { get; set; } = "";
    public decimal? SaleTaxable { get; set; }
    public decimal? SalePrice { get; set; }
    public decimal? PurchaseTaxable { get; set; }
    public decimal? EffectiveMarkupPercent { get; set; }
}