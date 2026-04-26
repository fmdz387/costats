using System.Reflection;
using costats.Application.Pricing;

namespace costats.Infrastructure.Pricing;

public sealed class EmbeddedPricingSnapshot
{
    private const string ResourceName = "costats.Infrastructure.Pricing.Resources.litellm-snapshot.json";

    public string SnapshotLabel => "litellm-snapshot embedded in assembly";

    public async Task<IReadOnlyDictionary<string, ModelPricing>> LoadAsync(CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();
        await using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded pricing snapshot '{ResourceName}' was not found.");

        return await LiteLLMPricingClient.ParseAsync(stream, cancellationToken).ConfigureAwait(false);
    }
}
