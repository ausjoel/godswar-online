namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<string> InspectOperatorBuffsAsync(CancellationToken cancellationToken)
    {
        var result = await _registry.InspectRuntimeBuffsForEditorAsync(_session, DateTimeOffset.UtcNow, cancellationToken);
        result["character"] = _character!.Name;
        return result.ToJsonString();
    }
    private async Task<string> RemoveOperatorBuffAsync(EditorBuffToken expected, CancellationToken cancellationToken)
    {
        var result = await _registry.RemoveRuntimeBuffForEditorAsync(_session, expected, DateTimeOffset.UtcNow, cancellationToken);
        result["character"] = _character!.Name;
        return result.ToJsonString();
    }
}
