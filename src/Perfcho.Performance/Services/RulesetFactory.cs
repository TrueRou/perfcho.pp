using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Taiko;

namespace Perfcho.Performance.Services;

public static class RulesetFactory
{
    public static Ruleset Create(string ruleset) => ruleset switch
    {
        "osu" => new OsuRuleset(),
        "taiko" => new TaikoRuleset(),
        "fruits" => new CatchRuleset(),
        "mania" => new ManiaRuleset(),
        _ => throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "unsupported_ruleset", "The requested ruleset is not supported.")
    };
}
