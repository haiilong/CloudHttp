namespace CloudHttp.Internal;

/// <summary>
/// Strategy for picking which underlying client index to use for the next request.
/// </summary>
internal interface IClientSelector
{
    /// <summary>Total number of clients.</summary>
    int Count { get; }

    /// <summary>
    /// Returns the index of the next client to use. When <paramref name="previousIndex"/> is
    /// provided (rotation scenario), the implementation tries to return a different index.
    /// </summary>
    int Select(int? previousIndex);

    /// <summary>Marks <paramref name="index"/> as transiently failing (health-aware mode only).</summary>
    void MarkDegraded(int index);

    /// <summary>Marks <paramref name="index"/> as healthy (health-aware mode only).</summary>
    void MarkHealthy(int index);
}
