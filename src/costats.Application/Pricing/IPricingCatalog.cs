namespace costats.Application.Pricing;

public interface IPricingCatalog
{
    ValueTask<ModelPricing?> LookupAsync(
        string modelId,
        string? providerHint = null,
        CancellationToken cancellationToken = default);
}
