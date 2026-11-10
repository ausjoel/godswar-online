using Godswar.Server.Application.Operators;
using Godswar.Server.Game;
using Godswar.Server.Packets;
namespace Godswar.Server.ProtocolChecks;
internal static class OperatorAuthorityChecks
{
    public const string CheckName="GM authority follows authenticated character flags and revocation";
    public static async Task RunAsync()
    {
        Check.Equal(TimeSpan.FromMinutes(5),PopulationNotice.Interval,"population cadence");
        var notice=PopulationNotice.Build(new[]{(1,(byte)0),(2,(byte)1),(2,(byte)1)});
        Check.Equal("Online: 2 | Athenians: 1 | Spartans: 1",notice,"population counts distinct online characters and native factions");
        Check.Equal((byte)1,PacketBuilder.PersonalGameLog(notice)[8],"population uses native personal system-message channel");
        var allowed=new[]{3}; var flag=false; var calls=0;
        Task<bool> Read(int account,int character,CancellationToken ct)
        { calls++; Check.Equal(3,account,"account is authenticated"); Check.Equal(7,character,"exact character selected"); return Task.FromResult(flag); }
        Check.True(await OperatorAuthority.AllowsAsync(true,false,3,7,allowed,null,default),"legacy allowlist remains available");
        Check.True(!await OperatorAuthority.AllowsAsync(true,true,3,7,allowed,Read,default),"legacy allowlist cannot bypass a cleared GM flag");
        flag=true;
        Check.True(await OperatorAuthority.AllowsAsync(true,true,3,7,Array.Empty<int>(),Read,default),"a flagged character needs no account allowlist");
        flag=false;
        Check.True(!await OperatorAuthority.AllowsAsync(true,true,3,7,allowed,Read,default),"next command observes revocation");
        Check.Equal(3,calls,"flag checked on every privileged command");
        Check.True(!await OperatorAuthority.AllowsAsync(false,true,3,7,allowed,Read,default),"global disabled denies");
        Check.True(!await OperatorAuthority.AllowsAsync(true,true,3,0,allowed,Read,default),"no selected character denies");
        Check.True(!await OperatorAuthority.AllowsAsync(true,true,3,7,allowed,null,default),"missing store denies");
        Check.True(!await OperatorAuthority.AllowsAsync(true,true,3,7,allowed,(_,_,_)=>throw new InvalidOperationException(),default),"store failure denies");
        try { await OperatorAuthority.AllowsAsync(true,true,3,7,allowed,(_,_,_)=>throw new OperationCanceledException(),default);throw new Exception("cancellation swallowed"); }
        catch(OperationCanceledException) { }
        OperatorControls.UpdatePowers(7,p=>p with {Invulnerable=true}); OperatorControls.ClearPowers(7);
        Check.True(OperatorControls.PowersOf(7).IsNone,"logout clears cached GM powers");
    }
}
