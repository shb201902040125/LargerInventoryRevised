using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace LargerInventory.Backend.Crafting;

internal static class RecipeConditionSnapshot
{
    // Match condition instances by identity. A localization key alone can also describe arbitrary mod code.
    private static readonly HashSet<Condition> WorldConditions = new(ReferenceEqualityComparer.Instance)
    {
        Condition.TimeDay, Condition.TimeNight, Condition.InClassicMode, Condition.InExpertMode,
        Condition.InMasterMode, Condition.InJourneyMode, Condition.Hardmode, Condition.PreHardmode,
        Condition.SmashedShadowOrb, Condition.CrimsonWorld, Condition.CorruptWorld,
        Condition.DrunkWorld, Condition.RemixWorld, Condition.NotTheBeesWorld, Condition.ForTheWorthyWorld,
        Condition.TenthAnniversaryWorld, Condition.DontStarveWorld, Condition.NoTrapsWorld, Condition.ZenithWorld,
        Condition.NotDrunkWorld, Condition.NotRemixWorld, Condition.NotNotTheBeesWorld, Condition.NotForTheWorthy,
        Condition.NotTenthAnniversaryWorld, Condition.NotDontStarveWorld, Condition.NotNoTrapsWorld, Condition.NotZenithWorld,
        Condition.Christmas, Condition.Halloween, Condition.BloodMoon, Condition.NotBloodMoon,
        Condition.Eclipse, Condition.NotEclipse, Condition.EclipseOrBloodMoon, Condition.NotEclipseAndNotBloodMoon,
        Condition.Thunderstorm, Condition.BirthdayParty, Condition.LanternNight, Condition.HappyWindyDay,
        Condition.DownedKingSlime, Condition.DownedEyeOfCthulhu, Condition.DownedEowOrBoc,
        Condition.DownedEaterOfWorlds, Condition.DownedBrainOfCthulhu, Condition.DownedQueenBee,
        Condition.DownedSkeletron, Condition.DownedDeerclops, Condition.DownedQueenSlime, Condition.DownedEarlygameBoss,
        Condition.DownedMechBossAny, Condition.DownedTwins, Condition.DownedDestroyer, Condition.DownedSkeletronPrime,
        Condition.DownedMechBossAll, Condition.DownedPlantera, Condition.DownedEmpressOfLight,
        Condition.DownedDukeFishron, Condition.DownedGolem, Condition.DownedMourningWood, Condition.DownedPumpking,
        Condition.DownedEverscream, Condition.DownedSantaNK1, Condition.DownedIceQueen, Condition.DownedCultist,
        Condition.DownedMoonLord, Condition.DownedClown, Condition.DownedGoblinArmy, Condition.DownedPirates,
        Condition.DownedMartians, Condition.DownedFrostLegion, Condition.DownedSolarPillar, Condition.DownedVortexPillar,
        Condition.DownedNebulaPillar, Condition.DownedStardustPillar, Condition.DownedOldOnesArmyAny,
        Condition.DownedOldOnesArmyT1, Condition.DownedOldOnesArmyT2, Condition.DownedOldOnesArmyT3,
        Condition.NotDownedKingSlime, Condition.NotDownedEyeOfCthulhu, Condition.NotDownedEowOrBoc,
        Condition.NotDownedEaterOfWorlds, Condition.NotDownedBrainOfCthulhu, Condition.NotDownedQueenBee,
        Condition.NotDownedSkeletron, Condition.NotDownedDeerclops, Condition.NotDownedQueenSlime,
        Condition.NotDownedTwins, Condition.NotDownedDestroyer, Condition.NotDownedSkeletronPrime,
        Condition.NotDownedPlantera, Condition.NotDownedEmpressOfLight, Condition.NotDownedDukeFishron,
        Condition.NotDownedGolem, Condition.NotDownedMourningWood, Condition.NotDownedPumpking,
        Condition.NotDownedEverscream, Condition.NotDownedSantaNK1, Condition.NotDownedIceQueen,
        Condition.NotDownedCultist, Condition.NotDownedMoonLord, Condition.NotDownedClown,
        Condition.NotDownedGoblinArmy, Condition.NotDownedPirates, Condition.NotDownedMartians,
        Condition.NotDownedFrostLegin, Condition.NotDownedSolarPillar, Condition.NotDownedVortexPillar,
        Condition.NotDownedNebulaPillar, Condition.NotDownedStardustPillar, Condition.NotDownedOldOnesArmyAny,
        Condition.NotDownedOldOnesArmyT1, Condition.NotDownedOldOnesArmyT2, Condition.NotDownedOldOnesArmyT3,
        Condition.BloodMoonOrHardmode, Condition.NightOrEclipse, Condition.Multiplayer,
        Condition.MoonPhaseFull, Condition.MoonPhaseWaningGibbous, Condition.MoonPhaseThirdQuarter,
        Condition.MoonPhaseWaningCrescent, Condition.MoonPhaseNew, Condition.MoonPhaseWaxingCrescent,
        Condition.MoonPhaseFirstQuarter, Condition.MoonPhaseWaxingGibbous, Condition.MoonPhasesQuarter0,
        Condition.MoonPhasesQuarter1, Condition.MoonPhasesQuarter2, Condition.MoonPhasesQuarter3,
        Condition.MoonPhasesHalf0, Condition.MoonPhasesHalf1, Condition.MoonPhasesEven, Condition.MoonPhasesOdd,
        Condition.MoonPhasesNearNew, Condition.MoonPhasesEvenQuarters, Condition.MoonPhasesOddQuarters,
        Condition.MoonPhases04, Condition.MoonPhases15, Condition.MoonPhases26, Condition.MoonPhases37
    };

    private static readonly Dictionary<Condition, Func<Player, bool>> PlayerConditions = new(ReferenceEqualityComparer.Instance)
    {
        [Condition.InDungeon] = player => player.ZoneDungeon,
        [Condition.InCorrupt] = player => player.ZoneCorrupt,
        [Condition.InHallow] = player => player.ZoneHallow,
        [Condition.InMeteor] = player => player.ZoneMeteor,
        [Condition.InJungle] = player => player.ZoneJungle,
        [Condition.InSnow] = player => player.ZoneSnow,
        [Condition.InCrimson] = player => player.ZoneCrimson,
        [Condition.InWaterCandle] = player => player.ZoneWaterCandle,
        [Condition.InPeaceCandle] = player => player.ZonePeaceCandle,
        [Condition.InTowerSolar] = player => player.ZoneTowerSolar,
        [Condition.InTowerVortex] = player => player.ZoneTowerVortex,
        [Condition.InTowerNebula] = player => player.ZoneTowerNebula,
        [Condition.InTowerStardust] = player => player.ZoneTowerStardust,
        [Condition.InDesert] = player => player.ZoneDesert,
        [Condition.InGlowshroom] = player => player.ZoneGlowshroom,
        [Condition.InUndergroundDesert] = player => player.ZoneUndergroundDesert,
        [Condition.InSkyHeight] = player => player.ZoneSkyHeight,
        [Condition.InOverworldHeight] = player => player.ZoneOverworldHeight,
        [Condition.InDirtLayerHeight] = player => player.ZoneDirtLayerHeight,
        [Condition.InRockLayerHeight] = player => player.ZoneRockLayerHeight,
        [Condition.InUnderworldHeight] = player => player.ZoneUnderworldHeight,
        [Condition.InBeach] = player => player.ZoneBeach,
        [Condition.InRain] = player => player.ZoneRain,
        [Condition.InSandstorm] = player => player.ZoneSandstorm,
        [Condition.InOldOneArmy] = player => player.ZoneOldOneArmy,
        [Condition.InGranite] = player => player.ZoneGranite,
        [Condition.InMarble] = player => player.ZoneMarble,
        [Condition.InHive] = player => player.ZoneHive,
        [Condition.InGemCave] = player => player.ZoneGemCave,
        [Condition.InLihzhardTemple] = player => player.ZoneLihzhardTemple,
        [Condition.InGraveyard] = player => player.ZoneGraveyard,
        [Condition.InAether] = player => player.ZoneShimmer,
        [Condition.InShoppingZoneForest] = player => player.ShoppingZone_Forest,
        [Condition.InBelowSurface] = player => player.ShoppingZone_BelowSurface,
        [Condition.InEvilBiome] = player => player.ZoneCrimson || player.ZoneCorrupt,
        [Condition.NotInEvilBiome] = player => !player.ZoneCrimson && !player.ZoneCorrupt,
        [Condition.NotInHallow] = player => !player.ZoneHallow,
        [Condition.NotInGraveyard] = player => !player.ZoneGraveyard,
        [Condition.NotInUnderworld] = player => !player.ZoneUnderworldHeight
    };

    internal static bool[] Capture(Recipe[] recipes, Player authorityPlayer)
    {
        bool server = Main.netMode == NetmodeID.Server;
        Player player = server ? authorityPlayer : Main.LocalPlayer;
        // Engine adjacency is updated during local inventory processing. On the server, scan the
        // actual tile map without calling Player.AdjTiles, which mutates player caches, runs global
        // hooks and can trigger the UI recipe search. Use the server's actual range and position.
        var nearby = player == null ? null : server ? ScanNearby(player) : new Nearby(player.adjTile,
            player.adjWater, player.adjLava, player.adjHoney, player.adjShimmer);
        var results = new bool[recipes.Length];
        var evaluated = new Dictionary<Condition, bool>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < recipes.Length; i++)
        {
            Recipe recipe = recipes[i];
            if (recipe.Disabled || recipe.requiredTile.Exists(tile => nearby == null || !nearby.HasTile(tile)))
                continue;

            bool available = true;
            foreach (Condition condition in recipe.Conditions)
            {
                if (!evaluated.TryGetValue(condition, out bool met))
                {
                    met = IsMet(condition, player, nearby);
                    evaluated.Add(condition, met);
                }
                if (!met)
                {
                    available = false;
                    break;
                }
            }
            results[i] = available;
        }
        return results;
    }

    private static bool IsMet(Condition condition, Player player, Nearby nearby)
    {
        if (WorldConditions.Contains(condition))
            return condition.IsMet();
        if (PlayerConditions.TryGetValue(condition, out var checkPlayer))
            return player != null && checkPlayer(player);
        if (ReferenceEquals(condition, Condition.NearWater))
            return nearby != null && (nearby.Water || nearby.HasTile(TileID.Sinks));
        if (ReferenceEquals(condition, Condition.NearLava))
            return nearby?.Lava == true;
        if (ReferenceEquals(condition, Condition.NearHoney))
            return nearby?.Honey == true;
        if (ReferenceEquals(condition, Condition.NearShimmer))
            return nearby?.Shimmer == true;

        // Unclassified conditions, including parameterized built-ins and arbitrary mod delegates,
        // are neutral. They can depend on unsynchronized instances and aren't safe to execute here.
        return true;
    }

    private sealed record Nearby(bool[] Tiles, bool Water, bool Lava, bool Honey, bool Shimmer)
    {
        internal bool HasTile(int tile) => (uint)tile < (uint)Tiles.Length && Tiles[tile];
    }

    private static Nearby ScanNearby(Player player)
    {
        var tiles = new bool[TileLoader.TileCount];
        bool water = false, lava = false, honey = false, shimmer = false;
        int rangeX = player.ateArtisanBread ? 8 : 4;
        int rangeY = player.ateArtisanBread ? 7 : 3;
        int centerX = (int)(player.Center.X / 16);
        int bottomY = (int)(player.Bottom.Y / 16);
        for (int x = centerX - rangeX; x <= centerX + rangeX; x++)
        {
            for (int y = bottomY - rangeY; y < bottomY + rangeY; y++)
            {
                if (!WorldGen.InWorld(x, y, 0))
                    continue;
                Tile tile = Main.tile[x, y];
                int type = tile.TileType;
                if (tile.HasTile && (uint)type < (uint)tiles.Length)
                {
                    tiles[type] = true;
                    SetVanillaAliases(tiles, type);
                    // Registered static mod aliases are safe to read. Global/instance adjacency
                    // hooks are deliberately omitted from the server's read-only scan.
                    var aliases = ModContent.GetModTile(type)?.AdjTiles;
                    if (aliases != null)
                        foreach (int alias in aliases)
                            if ((uint)alias < (uint)tiles.Length)
                                tiles[alias] = true;
                }

                water |= (tile.LiquidAmount > 200 && tile.LiquidType == LiquidID.Water) || TileID.Sets.CountsAsWaterSource[type];
                lava |= (tile.LiquidAmount > 200 && tile.LiquidType == LiquidID.Lava) || TileID.Sets.CountsAsLavaSource[type];
                honey |= (tile.LiquidAmount > 200 && tile.LiquidType == LiquidID.Honey) || TileID.Sets.CountsAsHoneySource[type];
                shimmer |= (tile.LiquidAmount > 200 && tile.LiquidType == LiquidID.Shimmer) || TileID.Sets.CountsAsShimmerSource[type];
            }
        }
        return new Nearby(tiles, water, lava, honey, shimmer);
    }

    private static void SetVanillaAliases(bool[] tiles, int type)
    {
        switch (type)
        {
            case TileID.Hellforge:
            case TileID.GlassKiln:
                tiles[TileID.Furnaces] = true;
                break;
            case TileID.AdamantiteForge:
                tiles[TileID.Furnaces] = true;
                tiles[TileID.Hellforge] = true;
                break;
            case TileID.MythrilAnvil:
                tiles[TileID.Anvils] = true;
                break;
            case TileID.Tables2:
            case TileID.PicnicTable:
            case TileID.DjinnLamp:
                tiles[TileID.Tables] = true;
                break;
            case TileID.AlchemyTable:
                tiles[TileID.Tables] = true;
                tiles[TileID.Bottles] = true;
                break;
        }
    }
}
