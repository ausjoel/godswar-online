using System.Collections.Immutable;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>Captured Fane arrivals and initial full-health actor placements.</summary>
internal static class WonderlandTerrainPolicy
{
    public const float SafeZoneRadius = 10f;
    // External capture SHA256 81EA111739F43E90319B64B777FE6A0F1D4CFEEB9E2DE34997692AAAD666DF44.
    // Sep13 complete run, seq60454..95730; unseen allied5 actors use its earlier same-day pass.
    // Full-health first appearances are used; chase/reappearance coordinates are excluded.
    private static readonly ImmutableArray<WonderlandIslandGeometry> Islands =
    [
        // Match the instance caller landing: free revival and recovery use this same entry point.
        new(1, new(169f, 0f, -216f), new(153f, 0f, -125f), new(172f, 0f, -158f), new(173.600006f, 0f, -153f),
            new(118f, -226.75f, 217.75f, -105f),
            [
                new(171.267059f, 0f, -151.701202f), // 22522: B_boss_xerxer_001
                new(172.889282f, 0f, -171.086075f), // 20870: B_normalC_AthensTower_001
                new(153.429138f, 0f, -165.536728f), // 20884: B_normalC_AthensTower_001
                new(160.247314f, 0f, -142.393082f), // 20898: B_normalC_AthensTower_001
                new(173.655319f, 0f, -139.802597f), // 20912: B_normalC_AthensTower_001
                new(171.014313f, 0f, -156.26889f), // 21696: B_normale_robber_003
                new(166.589905f, 0f, -153.016785f), // 21710: B_normale_robber_003
                new(179.320969f, 0f, -151.584335f), // 21724: B_normale_robber_003
                new(178.773132f, 0f, -152.622391f), // 21738: B_normale_robber_003
                new(173.726334f, 0f, -153.896545f), // 21752: B_normale_robber_003
                new(178.01149f, 0f, -152.728821f), // 21766: B_normale_robber_003
                new(169.866821f, 0f, -156.887192f), // 21780: B_normale_robber_003
                new(178.566513f, 0f, -153.871124f), // 21794: B_normale_robber_003
                new(163.703934f, 0f, -152.357788f), // 21626: B_normalg_flamingo_001
                new(172.984177f, 0f, -154.750122f), // 21640: B_normalg_flamingo_001
                new(191.695084f, 0f, -160.726547f), // 21486: B_normalg_famale_001
                new(179.362823f, 0f, -129.6763f), // 21514: B_normalg_famale_001
            ],
            []),
        new(2, new(20f, 0f, -194f), new(-5f, 0f, -168.5f), new(3f, 0f, -179f), new(23.6000004f, 0f, -151f),
            new(-29f, -209.75f, 35.75f, -144f),
            [
                new(24.461422f, 0f, -154.704025f), // 21472: B_bosse_dryad_001
                new(-16.909544f, 0f, -196.840271f), // 21654: B_bosse_gadsguard_007
                new(20.4765873f, 0f, -156.778641f), // 21808: B_normale_sprider_007
                new(23.6176758f, 0f, -159.110031f), // 21822: B_normale_sprider_007
                new(-12.2920151f, 0f, -196.610123f), // 21836: B_normale_sprider_007
                new(-13.6979799f, 0f, -196.58342f), // 21850: B_normale_sprider_007
                new(29.3329544f, 0f, -151.73584f), // 21402: B_normale_cyclops_004
                new(22.0184383f, 0f, -158.498383f), // 21416: B_normale_cyclops_004
                new(-14.0618849f, 0f, -193.631042f), // 21430: B_normale_cyclops_004
                new(-18.5331764f, 0f, -196.384613f), // 21444: B_normale_cyclops_004
                new(-14.4951611f, 0f, -196.555573f), // 22382: B_normalf_wraith_002
                new(-12.0721674f, 0f, -196.566254f), // 22396: B_normalf_wraith_002
                new(24.5403824f, 0f, -157.784576f), // 22410: B_normalf_wraith_002
                new(23.0533047f, 0f, -157.850998f), // 22424: B_normalf_wraith_002
                new(3.11342096f, 0f, -175.387314f), // 22438: B_normalg_wraith_003
                new(5.98658752f, 0f, -176.697357f), // 22452: B_normalg_wraith_003
                new(6.46269989f, 0f, -176.03215f), // 22466: B_normalg_wraith_003
                new(4.06871557f, 0f, -176.387482f), // 22480: B_normalg_wraith_005
                new(3.65039825f, 0f, -176.607208f), // 22494: B_normalg_wraith_005
                new(5.82026196f, 0f, -178.571396f), // 22508: B_normalg_wraith_005
            ],
            []),
        new(3, new(-63f, 0f, -115f), new(-153.5f, 0f, -100.300003f), new(-126f, 0f, -119f), new(-113.599998f, 0f, -129f),
            new(-160f, -153.25f, -57.25f, -83.5f),
            [
                new(-123.083916f, 0f, -133.69574f), // 22536: B_bosse_flamingo_001
                new(-131.070831f, 0f, -137.524994f), // 21934: B_normale_stymphalianbird_002
                new(-131.618073f, 0f, -140.408432f), // 21948: B_normale_stymphalianbird_002
                new(-129.87767f, 0f, -140.531296f), // 21962: B_normale_stymphalianbird_002
                new(-123.59034f, 0f, -120.346001f), // 21976: B_normale_stymphalianbird_002
                new(-125.676003f, 0f, -121.285011f), // 21990: B_normale_stymphalianbird_002
                new(-125.21051f, 0f, -122.848381f), // 22004: B_normale_stymphalianbird_002
                new(-109.285271f, 0f, -125.533348f), // 22018: B_normale_stymphalianbird_002
                new(-109.55584f, 0f, -126.258766f), // 22032: B_normale_stymphalianbird_002
                new(-109.993576f, 0f, -125.318573f), // 22046: B_normale_stymphalianbird_002
                new(-116.156898f, 0f, -137.266617f), // 22060: B_normale_stymphalianbird_002
                new(-117.240158f, 0f, -137.048904f), // 22074: B_normale_stymphalianbird_002
                new(-116.943298f, 0f, -135.055328f), // 22088: B_normale_stymphalianbird_002
                new(-130.54361f, 0f, -137.132538f), // 22102: B_normale_stymphalianbird_005
                new(-131.195984f, 0f, -140.499069f), // 22116: B_normale_stymphalianbird_005
                new(-132.210098f, 0f, -137.735931f), // 22130: B_normale_stymphalianbird_005
                new(-123.435799f, 0f, -119.744141f), // 22144: B_normale_stymphalianbird_005
                new(-125.215363f, 0f, -119.214699f), // 22158: B_normale_stymphalianbird_005
                new(-124.721092f, 0f, -122.910431f), // 22172: B_normale_stymphalianbird_005
                new(-108.408577f, 0f, -126.103088f), // 22186: B_normale_stymphalianbird_005
                new(-109.434669f, 0f, -127.184906f), // 22200: B_normale_stymphalianbird_005
                new(-108.488029f, 0f, -125.761292f), // 22214: B_normale_stymphalianbird_005
                new(-114.518532f, 0f, -138.63768f), // 22228: B_normale_stymphalianbird_005
                new(-114.166626f, 0f, -137.738831f), // 22242: B_normale_stymphalianbird_005
                new(-115.67923f, 0f, -135.309357f), // 22256: B_normale_stymphalianbird_005
                new(-101.365082f, 0f, -113.922089f), // 20940: B_normalC_AthensTower_001
            ],
            []),
        new(4, new(-144f, 0f, -21f), new(-159.5f, 0f, 94.3000031f), new(-164f, 0f, 65f), new(-160.600006f, 0f, 38f),
            new(-202f, -27.25f, -129.25f, 97.5f),
            [
                new(-159.584625f, 0f, 44.578598f), // 21682: B_bosse_male_001
                new(-180.860611f, 0f, 32.5231094f), // 21528: B_normale_female_001
                new(-168.985184f, 0f, 47.4541626f), // 21556: B_normale_female_001
                new(-154.796463f, 0f, 46.9366226f), // 21570: B_normale_female_001
                new(-142.279495f, 0f, 38.0789299f), // 21584: B_normale_female_001
                new(-150.759827f, 0f, 35.4587669f), // 21598: B_normale_female_001
                new(-159.687943f, 0f, 29.170496f), // 21612: B_normale_female_001
                new(-161.872452f, 0f, 44.0642357f), // 22550: B_normalD_wraith_002
                new(-163.989319f, 0f, 45.3773766f), // 22564: B_normalD_wraith_002
                new(-162.039246f, 0f, 45.9506645f), // 22578: B_normalD_wraith_002
            ],
            []),
        new(5, new(-186f, 0f, 195f), new(-76.5f, 0f, 146.300003f), new(-122f, 0f, 164f), new(-99f, 0f, 179f),
            new(-198.75f, 125.25f, -72.25f, 207.5f),
            [
                new(-130.757401f, 0f, 153.042816f), // 21024: B_bosse_greecewarrior_001
                new(-103.086006f, 0f, 182.052078f), // 21038: B_bosse_greecewarrior_002
                new(-137.397949f, 0f, 146.292145f), // 21094: B_normale_mage007
                new(-129.493851f, 0f, 154.687927f), // 21108: B_normale_mage007
                new(-123.750916f, 0f, 152.714828f), // 21122: B_normale_mage007
                new(-134.075043f, 0f, 157.806274f), // 21178: B_normale_mage017
                new(-123.93483f, 0f, 144.733627f), // 21192: B_normale_mage017
                new(-132.158936f, 0f, 150.782806f), // 21206: B_normale_mage017
                new(-97.0869522f, 0f, 185.584671f), // 21052: B_normale_mage006
                new(-100.824524f, 0f, 181.004227f), // 21066: B_normale_mage006
                new(-104.733864f, 0f, 172.418686f), // 21080: B_normale_mage006
                new(-105.372505f, 0f, 184.406616f), // 21136: B_normale_mage014
                new(-101.454681f, 0f, 179.193024f), // 21150: B_normale_mage014
                new(-107.605629f, 0f, 182.722672f), // 21164: B_normale_mage014
            ],
            []),
        new(6, new(116f, 0f, 211f), new(167.5f, 0f, 122.300003f), new(150f, 0f, 165f), new(155f, 0f, 176f),
            new(84f, 122.25f, 195.75f, 224f),
            [
                new(143.405167f, 0f, 167.828049f), // 20996: B_bosse_dragon_014
                new(128.595078f, 0f, 157.528564f), // 21864: B_normale_stub_003
                new(165.639786f, 0f, 154.63623f), // 21892: B_normale_stub_003
                new(140.00177f, 0f, 212.930664f), // 21906: B_normale_stub_003
            ],
            [new(144f, 0f, 145f), new(144f, 0f, 157f), new(156f, 0f, 157f), new(150f, 0f, 171f), new(162f, 0f, 179f), new(130f, 0f, 195f), new(144f, 0f, 191f), new(134f, 0f, 163f), new(98f, 0f, 187f)]),
        new(7, new(158f, 0f, 29f), new(214.5f, 0f, 39.2999992f), new(204f, 0f, 27f), new(176.5f, 0f, 17f),
            new(140f, -23.75f, 236.25f, 51.5f),
            [
                new(166.251572f, 0f, 13.0082455f), // 20968: B_bosse_dracoladon_003
                new(161.585159f, 0f, 20.9100075f), // 21220: B_normale_stymphalianbird_003
                new(170.502731f, 0f, 15.1531458f), // 21234: B_normale_stymphalianbird_003
                new(165.610855f, 0f, 21.0817413f), // 21248: B_normale_stymphalianbird_003
                new(162.122025f, 0f, 16.4284172f), // 21262: B_normale_stymphalianbird_003
                new(169.054291f, 0f, -4.65054703f), // 21276: B_normale_stymphalianbird_003
                new(183.35701f, 0f, 47.640564f), // 21332: B_norale_Tower_001
                new(170.993423f, 0f, 34.7377815f), // 21346: B_norale_Tower_001
                new(164.476624f, 0f, 18.8065376f), // 21360: B_normale_wraith_001
                new(156.995087f, 0f, 15.6393566f), // 21374: B_normale_wraith_001
                new(172.504822f, 0f, 11.8783379f), // 21388: B_normale_wraith_001
            ],
            []),
        new(8, new(56f, 0f, -112f), new(0f, 0f, 80f), new(0f, 0f, -19f), new(4f, 0f, 93f),
            new(-36f, -123.75f, 71.75f, 104f),
            [
                new(61.0597801f, 0f, -40.3367691f), // 20954: B_bosse_bull_001
                new(-12.8723574f, 0f, -38.4623795f), // 20982: B_bosse_pan_002
                new(-0.289722651f, 0f, 20.5801926f), // 21458: B_bossf_dracoladon_003
                new(0.612464309f, 0f, 46.7211685f), // 21668: B_bosse_kingofscorpion_01
                new(55.9639587f, 0f, -43.4180832f), // 22354: B_normalf_wraith_001
                new(54.6010361f, 0f, -44.0542679f), // 22368: B_normalf_wraith_001
                new(-8.35774708f, 0f, -35.6988602f), // 22326: B_normalf_wraith_001
                new(-7.18002844f, 0f, -38.8133354f), // 22340: B_normalf_wraith_001
                new(-0.584473431f, 0f, 22.3369617f), // 22270: B_normalf_wraith_001
                new(0.490166783f, 0f, 24.202692f), // 22284: B_normalf_wraith_001
                new(1.00087965f, 0f, 58.0379219f), // 22298: B_normalf_wraith_001
                new(0.0228137821f, 0f, 60.814888f), // 22312: B_normalf_wraith_001
                new(-0.176883012f, 0f, 93.9803467f), // 21010: B_normale_fairy_004
            ],
            []),
    ];

    public static WonderlandIslandGeometry GetIsland(int island) => island is >= 1 and <= 8
        ? Islands[island - 1] : throw new ArgumentOutOfRangeException(nameof(island));

    public static bool TryFindIsland(float x, float z, out int island)
    {
        island = Islands.FirstOrDefault(value => value.Bounds.Contains(x, z))?.Island ?? 0;
        return island != 0;
    }

    public static bool IsCombatArea(int island, float x, float z)
    {
        var geometry = GetIsland(island);
        // The captured seventh arrival sits beside a Putrid Bird. An authored
        // ten-unit exclusion here would disable an exactly placed native actor.
        return geometry.Bounds.Contains(x, z) &&
            (island == 7 || DistanceSquared(geometry.Entrance, x, z) > SafeZoneRadius * SafeZoneRadius) &&
            DistanceSquared(geometry.Exit, x, z) > SafeZoneRadius * SafeZoneRadius;
    }

    public static float DistanceSquared(WonderlandPosition position, float x, float z) =>
        (position.X - x) * (position.X - x) + (position.Z - z) * (position.Z - z);
}
