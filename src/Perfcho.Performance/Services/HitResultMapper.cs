using System.Reflection;
using System.Runtime.Serialization;
using osu.Game.Rulesets.Scoring;

namespace Perfcho.Performance.Services;

public static class HitResultMapper
{
    private static readonly IReadOnlyDictionary<string, HitResult> byName = Enum.GetValues<HitResult>()
        .Where(value => value != HitResult.None)
        .Select(value => (Value: value, Field: typeof(HitResult).GetField(value.ToString())!))
        .Where(item => item.Field.GetCustomAttribute<ObsoleteAttribute>() is null)
        .ToDictionary(
            item => item.Field.GetCustomAttribute<EnumMemberAttribute>()?.Value ?? item.Value.ToString().ToLowerInvariant(),
            item => item.Value,
            StringComparer.Ordinal);

    public static HitResult Parse(string name)
    {
        return byName.TryGetValue(name, out HitResult result)
            ? result
            : throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "unsupported_hit_result", $"Hit result {name} is not supported by this calculator release.");
    }
}
