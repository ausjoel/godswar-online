using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The gate and the payout of the quest experience appraisal.
    /// </summary>
    /// <remarks>
    /// The reference server answered the appraisal four times with a payload of
    /// four zero bytes and granted nothing, so neither its gate nor the encoding
    /// of a granted answer is attested by a capture. Both values below are this
    /// server's own and are marked 未实测 in
    /// <c>docs/quest-experience-appraisal-20260924.md</c>.
    /// </remarks>
    internal static class QuestAppraisalPolicy
    {
        /// <summary>
        /// The stock client's own text for a passed appraisal
        /// (<c>SM_L0_06</c>, "通过了任务经验加成的鉴定，完成任务时可额外获得10%的经验！")
        /// promises ten percent, so the payout is fixed at that.
        /// </summary>
        public const int BonusBasisPoints = 1_000;

        /// <summary>
        /// Minimum character level. One leaves the appraisal open to every
        /// character; raise it to gate the feature without touching the packet
        /// path.
        /// </summary>
        public const int MinimumLevel = 1;
    }

    /// <summary>Handles C2S 10093, the quest window's appraisal button.</summary>
    /// <remarks>
    /// The request carries no input: its two payload bytes were zero in all four
    /// captured clicks, so receiving the opcode is the whole request. The answer
    /// is always sent, granted or not, because the client's window waits for it.
    /// </remarks>
    private async Task HandleQuestAppraisalAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        _ = packet;
        if (_character is null || _session.BoundGamePrincipal is null)
        {
            return;
        }

        var bonusBasisPoints = 0;
        var newlyGranted = false;
        if (_questAppraisal is not null)
        {
            try
            {
                if (_character.Level >= QuestAppraisalPolicy.MinimumLevel)
                {
                    var result = await _questAppraisal.GrantAsync(
                        _character.Id,
                        QuestAppraisalPolicy.BonusBasisPoints,
                        cancellationToken);
                    bonusBasisPoints = result.BonusBasisPoints;
                    newlyGranted = result.NewlyGranted;
                }
                else
                {
                    bonusBasisPoints =
                        await _questAppraisal.ReadBonusBasisPointsAsync(
                            _character.Id,
                            cancellationToken);
                }

                if (newlyGranted)
                {
                    Console.Error.WriteLine(
                        $"[quest-appraisal] granted character={_character.Name} " +
                        $"level={_character.Level} " +
                        $"bonus={bonusBasisPoints}bp");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"[quest-appraisal] failed character={_character.Name} " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        await _session.SendAsync(
            PacketBuilder.QuestAppraisal(bonusBasisPoints),
            cancellationToken,
            "QuestAppraisal");

        if (newlyGranted)
        {
            await AnnounceQuestAppraisalAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Scales one quest hand-in's experience by the durable appraisal bonus.
    /// </summary>
    /// <remarks>
    /// The payout is read per hand-in rather than cached on the session: the
    /// bonus can be granted at any point in a session, and hand-ins are rare
    /// enough that one primary-key lookup is cheaper than a cache that could
    /// go stale. A storage failure pays the base reward rather than blocking
    /// the hand-in.
    /// </remarks>
    private async Task<int> ScaleQuestRewardExperienceAsync(
        int experience,
        CancellationToken cancellationToken)
    {
        if (experience <= 0 || _questAppraisal is null || _character is null)
        {
            return experience;
        }

        try
        {
            var bonusBasisPoints =
                await _questAppraisal.ReadBonusBasisPointsAsync(
                    _character.Id,
                    cancellationToken);
            if (bonusBasisPoints <= 0)
            {
                return experience;
            }

            var scaled = (int)Math.Clamp(
                (long)experience * (10_000 + bonusBasisPoints) / 10_000,
                0,
                int.MaxValue);
            Console.Error.WriteLine(
                $"[quest-appraisal] hand-in bonus character={_character.Name} " +
                $"base={experience} bonus={bonusBasisPoints}bp scaled={scaled}");
            return scaled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"[quest-appraisal] hand-in bonus read failed " +
                $"character={_character.Name} {ex.GetType().Name}: {ex.Message}");
            return experience;
        }
    }

    /// <summary>
    /// The native proclamation the client's own SrvMsg type 2 builds when a
    /// character passes a cross-level appraisal; this server sends the finished
    /// line instead, because the typed note form is not implemented here.
    /// </summary>
    private async Task AnnounceQuestAppraisalAsync(
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var text =
            $"{_character.Name} passed the quest experience appraisal: " +
            "+10% experience on every quest hand-in!";
        if (text.Length > PacketBuilder.CenteredRedAnnouncementMaximumTextLength ||
            text.Any(static character => character is < ' ' or > '~'))
        {
            // The stock proclamation renderer takes short printable ASCII only;
            // a name it cannot render costs the announcement, not the bonus.
            return;
        }

        await _registry.BroadcastToAllSessionsAsync(
            PacketBuilder.CenteredGreenAnnouncement(text),
            cancellationToken,
            label: "QuestAppraisalBroadcast");
    }
}
