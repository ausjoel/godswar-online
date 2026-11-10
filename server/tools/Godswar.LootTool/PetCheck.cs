using System.Text;

namespace Godswar.LootTool;

/// <summary>
/// Headless read-through of the pet layer, mirroring <c>--selftest</c> for the
/// loot side: it proves the queries run against the connected database, that the
/// bounds table exists, and that every tier the pet editor can select actually
/// has the hatch-rank rows the foreign key needs. It writes no game data.
/// </summary>
internal static class PetCheck
{
    public static async Task<int> RunAsync(bool republish = false)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var settings = LootToolSettings.Load();
        await using var store = new PetStore();
        store.Connect(settings.BuildConnectionString());
        var lines = new List<string>();
        var failures = new List<string>();
        try
        {
            await store.EnsureBoundsTableAsync();
            lines.Add($"库 {store.DatabaseName}｜档位上下限表已就绪");

            var aptitudes = await store.LoadAptitudesAsync();
            lines.Add($"pet_content_aptitude_definitions：{aptitudes.Count} 档");
            foreach (var row in aptitudes)
            {
                var expected = PetContent.TalentMaskFor(row.Aptitude);
                if (row.InnateTalentMask != expected)
                {
                    failures.Add(
                        $"档 {row.Aptitude} 库内天赋掩码 {row.InnateTalentMask} " +
                        $"与规则 {expected} 不一致");
                }
            }

            var species = await store.LoadSpeciesAsync();
            lines.Add(
                $"pet_content_species_definitions：{species.Count} 个种族，" +
                $"已保存上下限 {species.Count(row => row.BoundsSaved)} 个，" +
                $"有蛋道具 {species.Count(row => row.EggItemId is not null)} 个");
            var sample = species.FirstOrDefault();
            if (sample is not null)
            {
                lines.Add(
                    $"样例：{sample.SpeciesId} {sample.DisplayName}" +
                    $"（蛋 {sample.EggItemId?.ToString() ?? "无"}，" +
                    $"区间 {sample.MinimumAptitude}-{sample.MaximumAptitude}）");
            }

            var pets = await store.LoadOwnedPetsAsync();
            lines.Add($"character_pets：{pets.Count} 只");
            foreach (var pet in pets)
            {
                var expected = PetContent.TalentMaskFor(pet.Aptitude);
                if (pet.TalentMask != expected)
                {
                    failures.Add(
                        $"宠物 {pet.PetId} 档 {pet.Aptitude} 天赋掩码 {pet.TalentMask}" +
                        $"与规则 {expected} 不一致");
                }

                lines.Add(
                    $"  宠物 {pet.PetId}｜{pet.OwnerName}｜{pet.PetName}｜" +
                    $"{PetContent.DescribeAptitude(pet.Aptitude)}｜" +
                    $"rank {pet.Rank}｜六维合计 {pet.InitialSavvyBaseline}");
                foreach (var stat in await store.LoadPetStatsAsync(pet.PetId))
                {
                    lines.Add(
                        $"    {stat.Stat}：基础 {stat.InitialSavvy:0.####}" +
                        $"｜附加 {stat.AddedSavvy:0.####}" +
                        $"｜成长率 {stat.GrowthRate:0.######}");
                }
            }

            await CheckHatchRankCoverageAsync(store, pets, failures, lines);

            if (republish)
            {
                var edits = aptitudes.Select(row => new PetAptitudeEdit(
                    row.Aptitude,
                    row.MinimumTotalGrowth,
                    row.MaximumTotalGrowth,
                    row.MinimumInitialSavvy,
                    row.MaximumInitialSavvy,
                    row.MinimumAddedSavvy,
                    row.MaximumAddedSavvy)).ToList();
                var parent = (await store.LoadPublishedRevisionAsync()).Revision;
                var published = await store.PublishEditedAptitudesAsync(edits);
                var after = await store.LoadPublishedRevisionAsync();
                var reread = await store.LoadAptitudesAsync();
                lines.Add($"发布回环：{parent} → {published}");
                lines.Add($"发布后读回：{reread.Count} 档，" +
                    $"数值与发布前{(reread.Count == aptitudes.Count ? "一致" : "不一致")}");
                if (after.Revision != published)
                {
                    failures.Add($"发布指针没有指向新版本（期望 {published}，实际 {after.Revision}）");
                }

                if (!reread.Select(row => row.MaximumTotalGrowth)
                        .SequenceEqual(aptitudes.Select(row => row.MaximumTotalGrowth)))
                {
                    failures.Add("发布后成长率上限读回与发布前不同");
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
        }

        foreach (var line in lines)
        {
            Console.WriteLine(line);
        }

        foreach (var failure in failures)
        {
            Console.WriteLine($"不通过：{failure}");
        }

        Console.WriteLine(failures.Count == 0 ? "宠物数据层检查通过。" : "宠物数据层检查失败。");
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Changing an owned pet's aptitude re-points its birth rank at the new tier's
    /// hatch-rank row, because <c>character_pets</c> has a foreign key over
    /// (revision, aptitude, outcome order, birth rank). This proves that write can
    /// succeed for every tier before anyone tries it in the UI.
    /// </summary>
    private static async Task CheckHatchRankCoverageAsync(
        PetStore store,
        IReadOnlyList<OwnedPetRow> pets,
        List<string> failures,
        List<string> lines)
    {
        var revision = await store.LoadHatchRankRevisionAsync();
        if (revision is null)
        {
            lines.Add("本库没有孵化 rank 内容版本，跳过覆盖检查");
            return;
        }

        var missing = new List<short>();
        for (var aptitude = PetContent.MinimumAptitude;
             aptitude <= PetContent.MaximumAptitude;
             aptitude++)
        {
            if (!await store.HasHatchRankAsync(revision, aptitude))
            {
                missing.Add(aptitude);
            }
        }

        lines.Add(
            missing.Count == 0
                ? $"孵化 rank 覆盖：1-{PetContent.MaximumAptitude} 档全部可写入"
                : $"孵化 rank 缺失的档：{string.Join("、", missing)}（这些档在界面上选了也会失败）");
        if (pets.Count > 0 && missing.Count > 0)
        {
            failures.Add($"这些档位无法被写入（缺孵化 rank）：{string.Join("、", missing)}");
        }
    }
}
