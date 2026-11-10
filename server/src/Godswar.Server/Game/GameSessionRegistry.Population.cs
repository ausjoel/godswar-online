using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Events;
using Godswar.Server.Application.Npc;
using Godswar.Server.Application.Realms;
using Godswar.Server.Packets;
namespace Godswar.Server.Game;

internal static class PopulationNotice
{
    public static readonly TimeSpan Interval=TimeSpan.FromMinutes(EventContent.DefaultPopulationMinutes);
    public static string Build(IEnumerable<(int CharacterId,byte Camp)> characters)
    {
        var players=characters.DistinctBy(p=>p.CharacterId).ToArray();
        return $"Online: {players.Length} | Athenians: {players.Count(p=>p.Camp==1)} | Spartans: {players.Count(p=>p.Camp==0)}";
    }
}

internal sealed partial class GameSessionRegistry
{
    private static readonly TimeSpan NoticeTick=TimeSpan.FromSeconds(15);

    public async Task RunPopulationNoticesAsync(RealmCalendar calendar,CancellationToken cancellationToken)
    {
        using var timer=new PeriodicTimer(NoticeTick);
        var lastPopulation=DateTimeOffset.UtcNow;
        var previous=calendar.ToRealmTime(lastPopulation).DateTime;
        var movementSpeed=ServerRates.Current.Get(ServerRateKeys.MovementSpeed);
        await LoadMaskedNamesAsync(cancellationToken); // W1 Cypress Leave
        await LoadHermesHoodsAsync(cancellationToken); // ITEMS-HERMES
        try
        {
            while(await timer.WaitForNextTickAsync(cancellationToken))
            {
                var instant=DateTimeOffset.UtcNow;
                var now=calendar.ToRealmTime(instant).DateTime;
                var events=NpcContentOverrides.Current.Events;
                if(events.PopulationMinutes>0&&instant-lastPopulation>=TimeSpan.FromMinutes(events.PopulationMinutes))
                {
                    lastPopulation=instant;
                    await PublishPopulationNoticeOnceAsync(cancellationToken);
                }
                await GreetNewBoardSessionsAsync(cancellationToken); // the admin 2026-11-08
                await AccrueTimePrizesAsync(cancellationToken); // W1 prize claims
                await AccrueProfessionApAsync(cancellationToken); // PROF slice 1: online AP
                await ExpireProfessionCooldownsAsync(cancellationToken); // PROF slice 2: Type 1 cooldowns
                await FinishDuePetDispatchesAsync(cancellationToken); // W3 Magic Bell
                foreach(var notice in events.Notices)
                    if(ServerNoticePolicy.IsDue(notice,previous,now))
                        await PublishNoticeOnceAsync(notice,cancellationToken);
                previous=now;
                var currentSpeed=ServerRates.Current.Get(ServerRateKeys.MovementSpeed);
                if(currentSpeed!=movementSpeed)
                {
                    movementSpeed=currentSpeed;
                    foreach(var context in SnapshotWorldReadySessions())
                        {
                        try { _=await RunOperatorActionAsync(context.Session,new OperatorStatusRefreshAction("rates"),cancellationToken); }
                        catch(Exception error) when(error is not OperationCanceledException)
                        { Console.WriteLine($"[rates] speed refresh skipped: {error.GetType().Name}"); }
                    }
                }
            }
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { }
    }

    internal async Task<int> PublishNoticeOnceAsync(ServerNotice notice,CancellationToken cancellationToken)
    {
        byte[] packet;
        if(notice.Chat) // the admin 2026-11-10: e.g. "Event transporters now open" stays in the chat box
        {
            var chatted=await PublishChatLineAsync(new string(notice.Text.Where(static c=>c is >= ' ' and <= '~').ToArray()).Trim(),"ScheduledNoticeChat",cancellationToken);
            Console.WriteLine($"[notice] {notice.Id} chat delivered={chatted}");
            return chatted;
        }
        if(notice.Board)
        {
            var sent=await PublishBoardMessageAsync(notice.Text,cancellationToken);
            Console.WriteLine($"[notice] {notice.Id} board delivered={sent}");
            return sent;
        }
        try { packet=notice.Banner?PacketBuilder.CenteredRedAnnouncement(notice.Text):PacketBuilder.PersonalGameLog(notice.Text); }
        catch(ArgumentException error)
        {
            Console.WriteLine($"[notice] {notice.Id} refused: {error.GetType().Name}");
            return 0;
        }
        var delivered=0;
        foreach(var context in SnapshotWorldReadySessions())
        {
            try { await context.Session.SendAsync(packet,cancellationToken,"ScheduledNotice");delivered++; }
            catch(Exception error) when(error is not OperationCanceledException)
            { Console.WriteLine($"[notice] delivery skipped: {error.GetType().Name}"); }
        }
        Console.WriteLine($"[notice] {notice.Id} delivered={delivered}");
        return delivered;
    }

    internal async Task<int> PublishPopulationNoticeOnceAsync(CancellationToken cancellationToken)
    {
        var sessions=SnapshotWorldReadySessions();
        if(sessions.Length==0)return 0;
        var text=PopulationNotice.Build(sessions.Select(s=>(s.CharacterId,s.Character.Camp)));
        var delivered=0;
        // the admin 2026-11-08: a 1.4.3 client shows the line on its board (replacing the last one); older clients log it.
        // the admin 2026-11-10 (board.server_info): a message line inside the top box instead of line 1 above it.
        var kind=ServerMessageRouting.ToTopBox?'M':'O';
        foreach(var context in sessions)
            if(await SendBoardToAsync(context.Session,kind,text,cancellationToken)) delivered++;
        Console.WriteLine($"[population] {text} delivered={delivered}");
        return delivered;
    }
}
