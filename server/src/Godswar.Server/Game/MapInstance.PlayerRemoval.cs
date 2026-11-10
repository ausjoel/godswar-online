using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    // Acquire before entering the registry gate or world mailbox. The lease
    // drains old-scene sends and prevents new sends until removal commits.
    internal PlayerRemovalLease AcquirePlayerRemoval(ClientSession session)
    {
        while (true)
        {
            var viewer = _monsterViewers.GetOrAdd(
                session, static _ => new MonsterViewerState());
            viewer.TransitionGate.Wait();
            if (_monsterViewers.TryGetValue(session, out var current) &&
                ReferenceEquals(current, viewer))
            {
                return new PlayerRemovalLease(this, session, viewer);
            }
            viewer.TransitionGate.Release();
        }
    }

    internal sealed class PlayerRemovalLease : IDisposable
    {
        private readonly MapInstance _map;
        private readonly ClientSession _session;
        private MonsterViewerState? _viewer;

        internal PlayerRemovalLease(
            MapInstance map, ClientSession session, MonsterViewerState viewer)
        {
            _map = map;
            _session = session;
            _viewer = viewer;
        }

        internal bool Remove(MapInstance map, ClientSession session,
            out GameSessionContext? context)
        {
            var viewer = _viewer ??
                throw new ObjectDisposedException(nameof(PlayerRemovalLease));
            if (!ReferenceEquals(map, _map) ||
                !ReferenceEquals(session, _session))
            {
                throw new InvalidOperationException(
                    "Player removal requires its exact map and session lease.");
            }
            var removed = map.RemoveSessionAndShadow(session, out context);
            map._monsterViewers.TryRemove(
                new KeyValuePair<ClientSession, MonsterViewerState>(session, viewer));
            return removed;
        }

        public void Dispose()
        {
            var viewer = Interlocked.Exchange(ref _viewer, null);
            if (viewer is null)
            {
                return;
            }
            if (!_map.ContainsPlayer(_session))
            {
                _map._monsterViewers.TryRemove(
                    new KeyValuePair<ClientSession, MonsterViewerState>(_session, viewer));
            }
            viewer.TransitionGate.Release();
        }
    }
}
