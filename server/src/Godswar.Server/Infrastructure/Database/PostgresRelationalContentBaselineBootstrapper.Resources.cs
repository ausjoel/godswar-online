namespace Godswar.Server.Infrastructure.Database;

internal static partial class PostgresRelationalContentBaselineBootstrapper
{
    private static readonly BaselineResource ItemAttributesResource = new(
        "Godswar.Server.Infrastructure.DatabaseBaselines.005_item_attributes.sql",
        "E2405A6AF3CA4B927C02B7DF8E910858585D98014C83EA44CE0244E55ABA0C7B");

    private static readonly BaselineResource SkillsResource = new(
        "Godswar.Server.Infrastructure.DatabaseBaselines.006_skills_and_talents.sql",
        "E0E4A0D99FF8113432F90A442FE62F7EA325E86B084EF7F1281958589BC59D19");

    private static readonly BaselineResource NpcsResource = new(
        "Godswar.Server.Infrastructure.DatabaseBaselines.007_npcs.sql",
        "D4D7DEB430DBAA6FE334A0248E1692E0B0DE383EF517D06A4479CB8FB5F84C4A");

    private static readonly BaselineResource MapsResource = new(
        "Godswar.Server.Infrastructure.DatabaseBaselines.008_maps.sql",
        "DF54A88F936C60F2207E10EF3507D6F3CD85604D65903C2F9B75F62007BB1387");

    private static readonly BaselineResource MonstersResource = new(
        "Godswar.Server.Infrastructure.DatabaseBaselines.009_monsters.sql",
        "08BE82B59716E0521A7D9FAD50F0B3FC7B8B369C0BE4940A62FF8A40B608023B");
}
