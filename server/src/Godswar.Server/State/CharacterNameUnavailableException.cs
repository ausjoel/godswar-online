namespace Godswar.Server.State;

internal sealed class CharacterNameUnavailableException(string name, Exception? inner = null)
    : InvalidOperationException($"The character name '{name}' is already in use.", inner);
