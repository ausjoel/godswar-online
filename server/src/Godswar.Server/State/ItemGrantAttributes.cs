namespace Godswar.Server.State;

/// <summary>
/// The quality, grade and added attributes one granted item carries.
/// </summary>
/// <remarks>
/// These are the item instance's own columns in <c>character_items</c>, not a
/// property of the item template: the same equipment can arrive from a quest with
/// one set of attributes and from a monster drop with another. <see cref="None"/>
/// is what every grant used before this existed - a plain item with quality 1,
/// grade 1 and no added attributes - so a source that configures nothing keeps
/// behaving exactly as it did.
/// <para>
/// An empty slot is <c>NULL</c>, never zero: attribute id 0 is the real
/// <c>AttackA</c> ("physical attack I") a client renders as its own line, so a
/// slot written as 0 draws one phantom attribute on every item. The reference
/// items carry <c>NULL</c> in every slot they do not use, and so do we.
/// </para>
/// <para>
/// An id without a level is ordinary - most reference items set ids and leave
/// levels <c>NULL</c> - while a level without an id carries nothing and is
/// written as empty.
/// </para>
/// </remarks>
internal readonly record struct ItemGrantAttributes(
    short Quality,
    short Grade,
    short? Attribute1,
    short? AttributeLevel1,
    short? Attribute2,
    short? AttributeLevel2,
    short? Attribute3,
    short? AttributeLevel3,
    short? Attribute4,
    short? AttributeLevel4,
    short? Attribute5,
    short? AttributeLevel5)
{
    /// <summary>The unconfigured item: quality 1, grade 1, no attributes.</summary>
    public static ItemGrantAttributes None => new(
        1, 1,
        null, null,
        null, null,
        null, null,
        null, null,
        null, null);

    public bool HasAddedAttributes =>
        Attribute1.HasValue ||
        Attribute2.HasValue ||
        Attribute3.HasValue ||
        Attribute4.HasValue ||
        Attribute5.HasValue;

    /// <summary>
    /// The five slots as (attribute id, level) pairs, empty slots included, with
    /// any level that has no attribute dropped.
    /// </summary>
    public IEnumerable<(short? Id, short? Level)> Slots()
    {
        yield return Slot(Attribute1, AttributeLevel1);
        yield return Slot(Attribute2, AttributeLevel2);
        yield return Slot(Attribute3, AttributeLevel3);
        yield return Slot(Attribute4, AttributeLevel4);
        yield return Slot(Attribute5, AttributeLevel5);
    }

    private static (short? Id, short? Level) Slot(short? id, short? level) =>
        id.HasValue ? (id, level) : (null, null);
}
