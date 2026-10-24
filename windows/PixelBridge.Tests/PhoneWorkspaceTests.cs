using System.Text.Json.Nodes;
using PixelBridge.Core;

public class PhoneWorkspaceTests
{
    private static JsonObject Ok(JsonObject result)=>new(){["ok"]=true,["result"]=result};
    [Fact] public void IncomingRingStartsOnceAndStopsForAnswerAndPrivacy()
    {
        var model = new PhoneWorkspace((_, _) => Task.FromResult(Ok(new())));
        var rings = new List<string?>(); model.RingingChanged += call => rings.Add(call?.Id);
        JsonObject Event(string state) => JsonNode.Parse($$"""{"event":"phone.calls","calls":[{"id":"incoming","state":"{{state}}"}]}""")!.AsObject();
        model.OnEvent(Event("ringing")); model.OnEvent(Event("ringing")); model.OnEvent(Event("active"));
        Assert.Equal(new string?[] { "incoming", null }, rings);
        model.OnEvent(Event("ringing")); model.SetEnabled(false); model.OnEvent(Event("ringing"));
        Assert.Equal(new string?[] { "incoming", null, "incoming", null }, rings);
    }
    [Fact] public void CallControlsFollowConfirmedPhoneStateAndClearForPrivacy()
    {
        var model = new PhoneWorkspace((_, _) => Task.FromResult(Ok(new())));
        model.OnEvent(JsonNode.Parse("""{"event":"phone.calls","muted":true,"route":"bluetooth","canAddCall":true,"calls":[{"id":"call-a","state":"active","canHold":true,"canMerge":true,"canSwap":false}],"endpoints":[{"id":"mac-endpoint","name":"My Mac","route":"bluetooth","selected":true}]}""")!.AsObject());
        Assert.True(model.Muted); Assert.True(model.CanAddCall);
        Assert.True(Assert.Single(model.Calls).CanMerge);
        var endpoint = Assert.Single(model.Endpoints);
        Assert.Equal("mac-endpoint", endpoint.Id); Assert.True(endpoint.Selected);
        model.SetEnabled(false);
        Assert.Empty(model.Calls); Assert.Empty(model.Endpoints); Assert.False(model.Muted); Assert.False(model.CanAddCall);
        model.OnEvent(JsonNode.Parse("""{"event":"phone.calls","muted":true,"calls":[{"id":"late-call"}]}""")!.AsObject());
        Assert.Empty(model.Calls); Assert.False(model.Muted);
    }
    [Fact] public async Task CallsUseSelectedSimAndExactEndpointWithoutOptimisticMute()
    {
        var sent = new List<(string Op, JsonObject Args)>();
        var model = new PhoneWorkspace((op, args) => { sent.Add((op, System.Text.Json.JsonSerializer.SerializeToNode(args)!.AsObject())); return Task.FromResult(Ok(new())); });
        await model.CallAsync("+15550100", "sim-two");
        await model.CallActionAsync("phone.route", args: new { endpointId = "mac-endpoint", route = "bluetooth" });
        await model.CallActionAsync("phone.mute", args: new { enabled = true });
        Assert.Equal("sim-two", (string?)sent[0].Args["accountId"]);
        Assert.Equal("mac-endpoint", (string?)sent[1].Args["endpointId"]);
        Assert.False(model.Muted);
        model.OnEvent(JsonNode.Parse("""{"event":"phone.calls","muted":true,"calls":[]}""")!.AsObject());
        Assert.True(model.Muted);
    }
    [Fact] public async Task PhoneStatusProvidesAvailableSimAccounts()
    {
        var model = new PhoneWorkspace((_, _) => Task.FromResult(Ok(JsonNode.Parse("""{"dialer":true,"accounts":[{"id":"sim-one","name":"Personal","default":false},{"id":"sim-two","name":"Work","default":true}],"calls":{"calls":[]}}""")!.AsObject())));
        Assert.True(await model.RefreshStatusAsync()); Assert.True(model.Dialer);
        Assert.Equal("sim-two", Assert.Single(model.Accounts, x => x.Default).Id);
        model.Clear(); Assert.Empty(model.Accounts);
    }
    [Fact] public async Task AmbiguousSendIsQueriedWithoutSendingTwice()
    {
        var operations = new List<string>();
        var model = new PhoneWorkspace((op,args)=> { operations.Add(op);return Task.FromResult(Ok(new(){["state"]="queued"})); });
        Assert.True(await model.SendTextAsync("+15550100","hello"));
        Assert.False(await model.SendTextAsync("+15550100","hello"));
        Assert.Equal(new[]{"sms.send","sms.receipt"},operations);
    }
    [Fact] public async Task ConcurrentClicksHaveOneSendToken()
    {
        var pending = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously); int requests=0;
        var model = new PhoneWorkspace((op,args)=> { Interlocked.Increment(ref requests);return pending.Task; });
        var first=model.SendTextAsync("+15550100","hello");
        Assert.False(await model.SendTextAsync("+15550100","hello"));
        pending.SetResult(Ok(new(){["state"]="queued"}));Assert.True(await first);Assert.Equal(1,requests);
    }
    [Fact] public async Task StaleThreadResponseCannotReplaceCurrentConversation()
    {
        var a=new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);var b=new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);int n=0;
        var model=new PhoneWorkspace((op,args)=>Interlocked.Increment(ref n)==1?a.Task:b.Task);
        var first=model.LoadMessagesAsync(1);var second=model.LoadMessagesAsync(2);
        b.SetResult(Ok(JsonNode.Parse("""{"messages":[{"id":2,"body":"current","dateMs":2}]}""")!.AsObject()));await second;
        a.SetResult(Ok(JsonNode.Parse("""{"messages":[{"id":1,"body":"stale","dateMs":1}]}""")!.AsObject()));await first;
        Assert.Equal("current",Assert.Single(model.Messages).Body);
    }
    [Fact] public async Task PrivacyRejectsLatePrivateData()
    {
        var pending=new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model=new PhoneWorkspace((op,args)=>pending.Task);var request=model.SearchContactsAsync();model.SetEnabled(false);
        pending.SetResult(Ok(JsonNode.Parse("""{"contacts":[{"id":1,"name":"Example","number":"5550100"}]}""")!.AsObject()));
        Assert.False(await request);Assert.Empty(model.Contacts);
    }
    [Theory][InlineData("../file",false)][InlineData("a/b",false)][InlineData("a\\b",false)][InlineData("..",false)][InlineData("photo.jpg",true)][InlineData("hello world.txt",true)][InlineData("a\nfile",false)]
    public void PhoneFilesStayInsideDownloads(string name,bool valid)=>Assert.Equal(valid,PhoneCommands.ValidName(name));
    [Fact] public void ShellQuotingKeepsTextLiteral()=>Assert.Equal("'a'\\''b$()'",PhoneCommands.Quote("a'b$()"));
    [Theory][InlineData(0,0)][InlineData(90,270)][InlineData(-90,90)][InlineData(180,180)]
    public void QuaternionHeadingMatchesAndroidAxes(double turn,double heading)
    {
        var angle=turn*Math.PI/360;var r=CompassMath.FromQuaternion(new[]{0f,0f,(float)Math.Sin(angle),(float)Math.Cos(angle)},3)!;
        Assert.InRange(Math.Abs(CompassMath.ShortestDelta(r.Heading,heading)),0,.001);
    }
    [Fact] public void CompassFallsBackToTiltCompensatedMagnetometer()
    {
        var r=CompassMath.FromGravityAndMagnetic(new[]{0f,0f,9.81f},new[]{0f,40f,0f},2)!;
        Assert.Equal(0,r.Heading);Assert.Equal("N",r.Direction);Assert.InRange(r.FieldMicrotesla!.Value,39.99,40.01);
    }
    [Fact] public void CompassRejectsInvalidDataAndWrapsNorth()
    {
        Assert.Null(CompassMath.FromQuaternion(new[]{float.NaN,0,0,1},3));Assert.Null(CompassMath.FromQuaternion(new float[4],3));
        Assert.Equal(2,CompassMath.ShortestDelta(359,1));Assert.Equal(-2,CompassMath.ShortestDelta(1,359));
    }
    [Theory][InlineData("0","BOOTING",PhoneReadiness.Booting)][InlineData("1","RUNNING_LOCKED",PhoneReadiness.FirstUnlock)][InlineData("1","RUNNING_UNLOCKING",PhoneReadiness.FirstUnlock)][InlineData("1","RUNNING_UNLOCKED",PhoneReadiness.Ready)][InlineData("1","Unknown command",PhoneReadiness.Ready)]
    public void RebootWaitsWithoutConfusingUnlockedWithLocked(string boot,string user,PhoneReadiness expected)=>Assert.Equal(expected,PhoneBoot.Evaluate(boot,user));
}
