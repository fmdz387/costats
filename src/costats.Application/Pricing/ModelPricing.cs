using System.Text.Json.Serialization;

namespace costats.Application.Pricing;

public sealed record ModelPricing
{
    [JsonPropertyName("input_cost_per_token")]
    public decimal? InputCostPerToken { get; init; }

    [JsonPropertyName("input_cost_per_token_above_200k_tokens")]
    public decimal? InputCostPerTokenAbove200k { get; init; }

    [JsonPropertyName("output_cost_per_token")]
    public decimal? OutputCostPerToken { get; init; }

    [JsonPropertyName("output_cost_per_token_above_200k_tokens")]
    public decimal? OutputCostPerTokenAbove200k { get; init; }

    [JsonPropertyName("reasoning_output_cost_per_token")]
    public decimal? ReasoningOutputCostPerToken { get; init; }

    [JsonPropertyName("reasoning_output_cost_per_token_above_200k_tokens")]
    public decimal? ReasoningOutputCostPerTokenAbove200k { get; init; }

    [JsonPropertyName("reasoning_cost_per_token")]
    public decimal? ReasoningCostPerToken { get; init; }

    [JsonPropertyName("output_cost_per_reasoning_token")]
    public decimal? OutputCostPerReasoningToken { get; init; }

    [JsonPropertyName("cache_creation_input_token_cost")]
    public decimal? CacheCreationInputTokenCost { get; init; }

    [JsonPropertyName("cache_creation_input_token_cost_above_200k_tokens")]
    public decimal? CacheCreationInputTokenCostAbove200k { get; init; }

    [JsonPropertyName("cache_creation_input_token_cost_above_1hr")]
    public decimal? CacheCreationInputTokenCostAbove1Hr { get; init; }

    [JsonPropertyName("cache_read_input_token_cost")]
    public decimal? CacheReadInputTokenCost { get; init; }

    [JsonPropertyName("cache_read_input_token_cost_above_200k_tokens")]
    public decimal? CacheReadInputTokenCostAbove200k { get; init; }

    [JsonPropertyName("litellm_provider")]
    public string? LiteLlmProvider { get; init; }
}
