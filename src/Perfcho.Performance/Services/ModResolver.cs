using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using osu.Framework.Bindables;
using osu.Game.Configuration;
using osu.Game.Extensions;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Utils;
using Perfcho.Performance.Contracts;

namespace Perfcho.Performance.Services;

public sealed record ResolvedMods(Mod[] Values, string CanonicalJson);

public static class ModResolver
{
    public static ResolvedMods Resolve(Ruleset ruleset, PerformanceMetadata metadata, bool isLegacyScore)
    {
        JsonSerializer settingsSerializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            Culture = CultureInfo.InvariantCulture,
            Converters = [new StringEnumConverter()]
        });
        var mods = new List<Mod>();
        foreach (CanonicalMod input in metadata.Mods!)
            mods.Add(ResolveOne(ruleset, input, settingsSerializer));

        if (isLegacyScore && mods.All(mod => mod.Acronym != "CL"))
        {
            Mod? classic = ruleset.CreateModFromAcronym("CL");
            if (classic is not null)
                mods.Add(classic);
        }

        if (!ModUtils.CheckCompatibleSet(mods, out List<Mod>? invalid))
        {
            string names = string.Join(", ", invalid.Select(mod => mod.Acronym).Distinct(StringComparer.Ordinal).Order());
            throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "incompatible_mods", $"The mod combination is incompatible: {names}.");
        }

        Mod[] ordered = mods.OrderBy(mod => mod.Acronym, StringComparer.Ordinal).ToArray();
        var canonical = new JArray(ordered.Select(mod => ToCanonicalJson(mod, settingsSerializer)));
        return new ResolvedMods(ordered, canonical.ToString(Formatting.None));
    }

    private static Mod ResolveOne(Ruleset ruleset, CanonicalMod input, JsonSerializer settingsSerializer)
    {
        Mod? reference = ruleset.CreateModFromAcronym(input.Acronym!);
        if (reference is null)
            throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "unsupported_mod", $"Mod {input.Acronym} is not supported by {ruleset.ShortName}.");

        Dictionary<string, System.Reflection.PropertyInfo> settingProperties = reference.GetSettingsSourceProperties()
            .Select(item => item.Item2)
            .ToDictionary(property => property.Name.ToSnakeCase(), StringComparer.Ordinal);
        JObject settings = input.Settings ?? new JObject();
        foreach (JProperty setting in settings.Properties())
        {
            if (!settingProperties.ContainsKey(setting.Name))
                throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "unsupported_mod_setting", $"Mod {input.Acronym} does not support setting {setting.Name}.");
        }

        var apiMod = new APIMod
        {
            Acronym = input.Acronym!,
            Settings = settings.Properties().ToDictionary(
                property => property.Name,
                property => property.Value.ToObject<object>(settingsSerializer)!,
                StringComparer.Ordinal)
        };
        Mod resolved = apiMod.ToMod(ruleset);

        foreach (JProperty supplied in settings.Properties())
        {
            object bindable = settingProperties[supplied.Name].GetValue(resolved)!;
            object? actual = bindable.GetUnderlyingSettingValue();
            if (actual is null)
            {
                if (supplied.Value.Type != JTokenType.Null)
                    throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "invalid_mod_setting", $"Mod {input.Acronym} setting {supplied.Name} has an invalid value.");
                continue;
            }
            object? expected;
            try
            {
                expected = supplied.Value.ToObject(actual.GetType(), settingsSerializer);
            }
            catch (Exception exception) when (exception is JsonException or FormatException or InvalidCastException)
            {
                throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "invalid_mod_setting", $"Mod {input.Acronym} setting {supplied.Name} has an invalid value.", exception);
            }

            if (!Equals(actual, expected))
                throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "invalid_mod_setting", $"Mod {input.Acronym} setting {supplied.Name} is outside the supported range.");
        }

        if (resolved is IHasSeed seeded && seeded.Seed.Value is null)
            throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "mod_seed_required", $"Mod {input.Acronym} requires an explicit seed for deterministic calculation.");

        return resolved;
    }

    private static JObject ToCanonicalJson(Mod mod, JsonSerializer settingsSerializer)
    {
        var apiMod = new APIMod(mod);
        var result = new JObject { ["acronym"] = apiMod.Acronym };
        if (apiMod.Settings.Count > 0)
        {
            var settings = new JObject();
            foreach ((string name, object value) in apiMod.Settings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                settings[name] = JToken.FromObject(value, settingsSerializer);
            result["settings"] = settings;
        }
        return result;
    }
}
