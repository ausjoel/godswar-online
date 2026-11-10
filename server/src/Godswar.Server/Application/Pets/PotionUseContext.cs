using Godswar.Server.State;

namespace Godswar.Server.Application.Pets;

/// <summary>
/// Trusted owning-session context, not a field decoded from the client's activation packet.
/// The durable executor must resolve the locked item's native effect itself, require its
/// template to match, and CAS the before-vitals under the envelope's player ownership fence.
/// </summary>
internal readonly record struct PotionUseContext(uint ItemTemplateId, PotionVitalsSnapshot Before)
{
    public bool IsValid => ItemTemplateId > 0 && Before.IsValid && Before.Hp > 0 &&
        Before.Revision <= long.MaxValue - 2;
}
