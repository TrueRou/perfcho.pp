namespace Perfcho.Performance.Contracts;

public sealed record CalculationResult(
    double StarRating,
    int MaxCombo,
    IReadOnlyDictionary<string, object?> DifficultyAttributes,
    double PerformancePoints,
    IReadOnlyDictionary<string, object?> PerformanceBreakdown);
