using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

/// <summary>
/// Short-lived proof that an authenticated living character opened an exact
/// ordinary Transporter menu while near that NPC in the same world instance.
/// </summary>
internal sealed record TransporterDialogueContext(
    int AccountId,
    int CharacterId,
    string NpcKey,
    uint NpcInteractionId,
    short SourceMapId,
    WorldInstanceId SourceWorldInstanceId,
    DateTimeOffset ExpiresAt)
{
    /// <summary>B22 (d32): the buttons this menu actually showed; a click on any other sub-id is refused.</summary>
    public IReadOnlyList<int>? MenuSubIds { get; init; }

    /// <summary>B22 B26-T: the page-1 button whose page is open (null = page 1 is showing).</summary>
    public int? PageSubId { get; init; }

    /// <summary>B22 B26-T: the buttons that page showed; a page click on any other sub-id is refused.</summary>
    public IReadOnlyList<int>? PageMenuSubIds { get; init; }
}
