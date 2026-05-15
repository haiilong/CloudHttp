namespace CloudHttp;

/// <summary>
/// Strategy used to pick which underlying <see cref="HttpClient"/> serves the next request.
/// </summary>
public enum DistributionMode
{
    /// <summary>Pick clients in strict rotation (default).</summary>
    RoundRobin,

    /// <summary>
    /// Pick clients with probability proportional to <see cref="ClientDistributionOptions.ClientWeights"/>.
    /// </summary>
    Weighted,

    /// <summary>
    /// Round-robin over the full client set, skipping any client recently marked as transiently
    /// failing for <see cref="ClientDistributionOptions.HealthDegradedTimeout"/>.
    /// </summary>
    HealthAware,
}