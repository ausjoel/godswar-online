namespace Godswar.Server.Game;

internal enum LegacyInstanceOpalConsentValidationStatus : byte
{
    Ready = 1,
    PartyChanged = 2,
    MissingConsent = 3
}
