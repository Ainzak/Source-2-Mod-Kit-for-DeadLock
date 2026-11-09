namespace S2ModKit.Desktop.Tests;

/// <summary>Ability buttons: an ability's clip is the model's animation named after its animation-graph action.</summary>
public sealed class HeroAbilityTests
{
    // Animation names of the Wraith model (as the preview lists them), trimmed.
    private static readonly string[] WraithClips =
    [
        "ability_card_trick_cast", "ability_generic_cast", "idle_stand",
        "models/heroes_wip/wraith/clips/ability_card_trick.vnmclip", "models/heroes_wip/wraith/clips/ability_card_trick_add.vnmclip",
        "models/heroes_wip/wraith/clips/ability_card_trick_alt.vnmclip", "models/heroes_wip/wraith/clips/ability_full_auto.vnmclip",
        "models/heroes_wip/wraith/clips/ability_project_mind.vnmclip", "models/heroes_wip/wraith/clips/ability_telekinesis.vnmclip",
    ];

    [Fact]
    public void ActionsNameTheirClipsExactlyOrByTheShortestLongerName()
    {
        HeroAbility Ability(params string[] actions) => new(1, "id", "Name", actions);
        Assert.Equal("models/heroes_wip/wraith/clips/ability_card_trick.vnmclip", HeroAbilities.ClipFor(Ability("ability_card_trick_cast_completed"), WraithClips));
        Assert.Equal("models/heroes_wip/wraith/clips/ability_full_auto.vnmclip", HeroAbilities.ClipFor(Ability("ability_full_auto_cast_completed"), WraithClips));
        Assert.Equal("models/heroes_wip/wraith/clips/ability_telekinesis.vnmclip", HeroAbilities.ClipFor(Ability("ability_telekinesis_casting", "ability_telekinesis_cast_completed"), WraithClips));
        Assert.Equal("models/x/clips/ability_flame_dash_intro.vnmclip",
            HeroAbilities.ClipFor(Ability("ability_flame_dash_casting"), ["models/x/clips/ability_flame_dash_intro.vnmclip", "models/x/clips/ability_flame_dash_lean_center.vnmclip"]));
        Assert.Null(HeroAbilities.ClipFor(Ability("napalm_casting"), WraithClips));                // no clip: no button
        Assert.Null(HeroAbilities.ClipFor(Ability(), WraithClips));
        Assert.Equal("ability_card_trick", HeroAbilities.Stem("Ability_Card_Trick_Casting"));
    }

    [Fact]
    public void SquashedNamesMatchByActionIdOrNameAndTheMainClipWins()
    {
        string[] yamato =
        [
            "models/heroes_wip/yamato/clips/ability_powerslash_charge.vnmclip", "models/heroes_wip/yamato/clips/ability_powerslash_cast.vnmclip",
            "models/heroes_wip/yamato/clips/ability_powerslash_in_air_cast.vnmclip", "models/heroes_wip/yamato/clips/ability_flyingstrike_fly_loop.vnmclip",
            "models/heroes_wip/yamato/clips/ability_flyingstrike_attack.vnmclip", "models/heroes_wip/yamato/clips/ability_shadowtransform_explosion.vnmclip",
            "models/heroes_wip/yamato/clips/ability_shadowtransform_intro.vnmclip", "models/heroes_wip/yamato/clips/ability_healingslash_cast.vnmclip",
            "models/heroes_wip/yamato/clips/cast_start.vnmclip",
        ];
        Assert.EndsWith("ability_powerslash_cast.vnmclip", HeroAbilities.ClipFor(new(1, "citadel_ability_power_slash", "Power Slash", ["powerslash_casting", "powerslash_completed"]), yamato));
        Assert.EndsWith("ability_flyingstrike_attack.vnmclip", HeroAbilities.ClipFor(new(2, "citadel_ability_flying_strike", "Flying Slash", []), yamato));    // by its id
        Assert.EndsWith("ability_healingslash_cast.vnmclip", HeroAbilities.ClipFor(new(3, "citadel_ability_healing_slash", "Crimson Slash", ["healing_slash_casting"]), yamato));
        Assert.EndsWith("ability_shadowtransform_intro.vnmclip", HeroAbilities.ClipFor(new(4, "citadel_ability_infinity_slash", "Shadow Transformation", ["infinityslash_cast_completed"]), yamato));   // by its name
    }

    [Fact]
    public void AnAbilitysPhasesPlayInOrderWithoutVariants()
    {
        string[] clips =
        [
            "m/ability_flyingstrike_attack.vnmclip", "m/ability_flyingstrike_fly_intro.vnmclip", "m/ability_flyingstrike_fly_loop.vnmclip",
            "m/ability_flyingstrike_shoot.vnmclip", "m/ability_flyingstrike_shoot_loop.vnmclip", "m/ability_powerslash_cast.vnmclip",
            "m/ability_powerslash_charge.vnmclip", "m/ability_powerslash_in_air_cast.vnmclip", "m/ability_powerslash_in_air_charge.vnmclip",
            "m/ability_shadowtransform_explosion.vnmclip", "m/ability_shadowtransform_intro.vnmclip", "m/ability_card_trick.vnmclip",
            "m/ability_card_trick_add.vnmclip", "m/ability_card_trick_alt.vnmclip", "m/throw.vnmclip",
        ];
        static string[] Leaves(IReadOnlyList<string> names) => [.. names.Select(Path.GetFileNameWithoutExtension).OfType<string>()];
        Assert.Equal(["ability_powerslash_charge", "ability_powerslash_cast"], Leaves(HeroAbilities.PhasesFor(new(1, "citadel_ability_power_slash", "Power Slash", ["powerslash_casting"]), clips)));
        Assert.Equal(["ability_flyingstrike_shoot", "ability_flyingstrike_shoot_loop", "ability_flyingstrike_fly_intro", "ability_flyingstrike_fly_loop", "ability_flyingstrike_attack"],
            Leaves(HeroAbilities.PhasesFor(new(2, "citadel_ability_flying_strike", "Flying Slash", []), clips)));
        Assert.Equal(["ability_shadowtransform_intro", "ability_shadowtransform_explosion"], Leaves(HeroAbilities.PhasesFor(new(4, "citadel_ability_infinity_slash", "Shadow Transformation", []), clips)));
        Assert.Equal(["ability_card_trick"], Leaves(HeroAbilities.PhasesFor(new(1, "citadel_ability_card_toss", "Card Trick", ["ability_card_trick_cast_completed"]), clips)));
        Assert.Equal(["throw"], Leaves(HeroAbilities.PhasesFor(new(1, "x", "Flask", ["throw"]), clips)));      // a generic clip is one phase
        Assert.Empty(HeroAbilities.PhasesFor(new(3, "citadel_ability_passive_beefy", "Infernal Resilience", []), clips));   // a passive: no button
    }

    /// <summary>Report of every hero's ability clips from the game data (S2MODKIT_TEST_GAME and S2MODKIT_TEST_ABILITIES_REPORT);
    /// no window is opened.</summary>
    [Fact]
    public void EveryHerosAbilityClipsAreReported()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        var report = Environment.GetEnvironmentVariable("S2MODKIT_TEST_ABILITIES_REPORT");
        if (string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(report))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME and S2MODKIT_TEST_ABILITIES_REPORT to write the report.");
            return;
        }

        var models = HeroAbilities.Read(game, report + ".cache.json")!;
        var heroes = models.GroupBy(pair => pair.Value).Select(group => (Model: group.First().Key, Abilities: group.Key)).OrderBy(item => item.Model, StringComparer.Ordinal).ToArray();
        File.WriteAllLines(report + ".effects.txt", heroes.Select(hero => $"{hero.Model}: " + string.Join("; ", hero.Abilities.Select(ability => $"{ability.Name} {ability.EffectList.Count}"))));
        heroes = [.. heroes.Select(hero => (hero.Model, (IReadOnlyList<HeroAbility>)[.. hero.Abilities.Where(ability => ability.Slot is >= 1 and <= 4)]))];
        File.WriteAllLines(report, [$"{heroes.Sum(hero => hero.Abilities.Count(ability => ability.PlayList.Count > 0))} of {heroes.Sum(hero => hero.Abilities.Count)} abilities have a clip",
            .. heroes.Select(hero => $"{hero.Model}: " + string.Join("; ", hero.Abilities.Select(ability => $"{ability.Slot} {ability.Name} = {(ability.PlayList.Count == 0 ? "-" : string.Join(" > ", ability.PlayList.Select(Path.GetFileNameWithoutExtension)))}")))]);
    }

    /// <summary>Live check on the game data (S2MODKIT_TEST_GAME): every hero has four abilities with names, and Wraith's actions
    /// map to its four clips. No window is opened.</summary>
    [Fact]
    public void TheGameDataGivesEveryHeroFourNamedAbilities()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this live check on the game data.");
            return;
        }

        var cache = Path.Combine(Path.GetTempPath(), "s2mk-abilities-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var models = HeroAbilities.Read(game, cache);
            Assert.NotNull(models);
            var wraith = HeroAbilities.Of(models, "models/heroes_wip/wraith/wraith.vmdl_c");
            Assert.Equal(["Card Trick", "Project Mind", "Full Auto", "Telekinesis", "Weapon", "Other effects"], wraith.Select(ability => ability.Name));
            Assert.All(wraith, ability => Assert.NotEmpty(ability.EffectList));
            Assert.All(wraith.Where(ability => ability.Slot is >= 1 and <= 4), ability => Assert.NotNull(HeroAbilities.ClipFor(ability, WraithClips)));
            Assert.Equal(models.Count, HeroAbilities.Read(game, cache)!.Count);                        // read back from the cache
        }
        finally
        {
            File.Delete(cache);
        }
    }
}
