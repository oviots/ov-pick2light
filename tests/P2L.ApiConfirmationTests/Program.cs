using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Mqtt;
using P2L.Mqtt.Configuration;
using P2L.Shared.Models;

static void Check(bool value,string message){if(!value)throw new Exception(message);}
static async Task Until(Func<bool> value){using var stop=new CancellationTokenSource(20000);while(!value())await Task.Delay(20,stop.Token);}
const string token="p2l-fixture-token-0123456789abcdef";
var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
await using var server=builder.Build();
var calls=0;var received=new HashSet<string>();var sync=new object();
string mode="stored";bool cancelled=false;
server.MapPost("/events",async(HttpContext context)=>{
    Interlocked.Increment(ref calls);
    Check(context.Request.Headers.Authorization=="Bearer "+token,"Bearer token missing");
    using var doc=await JsonDocument.ParseAsync(context.Request.Body);
    var ids=doc.RootElement.GetProperty("events").EnumerateArray().Select(e=>e.GetProperty("eventId").GetString()!).ToArray();
    if(mode=="hold") {try{await Task.Delay(30000,context.RequestAborted);}catch(OperationCanceledException){cancelled=true;}return;}
    if(mode=="401"){context.Response.StatusCode=401;return;}
    if(mode=="malformed"){await context.Response.WriteAsync("{}");return;}
    if(mode=="unknown"){await context.Response.WriteAsJsonAsync(new{storedEventIds=new[]{Guid.NewGuid().ToString("N")}});return;}
    if(mode=="partial"){await context.Response.WriteAsJsonAsync(new{storedEventIds=ids.Take(1)});return;}
    lock(sync){foreach(var id in ids)received.Add(id);}
    if(mode=="lost") {mode="stored";context.Abort();return;}
    // Re-sending an already-stored event receives the same acknowledgement.
    await context.Response.WriteAsJsonAsync(new{storedEventIds=ids});
});
await server.StartAsync();var url=server.Urls.Single()+"/events";
var dir=Path.Combine(Path.GetTempPath(),"p2l-api-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
await using var gateway=new AioiGateway("127.0.0.1",1,NullLogger.Instance);
var registry=new LightModuleRegistry();using var engine=new PickingEngine(gateway,registry,NullLogger<PickingEngine>.Instance);
var labels=new DeviceLabelStore(Path.Combine(dir,"devices.db"),NullLogger<DeviceLabelStore>.Instance);
var scan=new DeviceScanService(gateway,registry,NullLogger<DeviceScanService>.Instance);
MqttBridgeService Bridge(ConfirmationOutbox outbox,bool enabled=true)=>new(new MqttSettings{Enabled = true,
    Host="127.0.0.1",Port=1,Tls=new(){Enabled=false},OfflineBufferPath=Path.Combine(dir,"offline.jsonl"),
    ConfirmApi=new(){Enabled=enabled,Url=url,Token=token,PollIntervalMs=100,BatchSize=2}},
    gateway,engine,registry,new PreemptionGate(),null,labels,scan,NullLoggerFactory.Instance,confirmations:outbox);
LightModuleState Record(ConfirmationOutbox outbox){var state=new LightModuleState{Address="0001",BlockNo="01",CorrelationId="SESSION",PickingDetailId="1".PadLeft(24,'0')};outbox.Record(new(){Address="0001"},state);return state;}
try{
    // Defaults and remote settings remain disabled unless explicitly enabled.
    Check(!ConfirmApiSettings.FromRemote(JsonDocument.Parse("{}").RootElement).Enabled,"Old config enabled API");
    var outbox=new ConfirmationOutbox(Path.Combine(dir,"off.db"));Record(outbox);
    var bridge=Bridge(outbox,false);await bridge.StartAsync();await Task.Delay(250);Check(calls==0&&outbox.ReadPending().Count==1,"Disabled API sent or deleted work");await bridge.DisposeAsync();
    Console.WriteLine("PASS: API disabled preserves the MQTT path; no HTTP traffic");

    outbox=new(Path.Combine(dir,"accepted.db"));mode="lost";var original=Record(outbox);bridge=Bridge(outbox);await bridge.StartAsync();
    await Until(()=>calls>=1);await Task.Delay(200);Check(outbox.ReadPending().Count==1,"Lost response erased SQLite");
    await Until(()=>outbox.ReadPending().Count==0);
    lock(sync)Check(received.Count==1&&received.Contains(original.AssignmentId),"Retry changed event identity");
    Check(!bridge.IsConnected,"Fixture accidentally connected to an MQTT broker");await bridge.DisposeAsync();
    Console.WriteLine("PASS: API works while MQTT is down; lost first response retries the same stored event and deletes SQLite on repeat ACK");

    foreach(var invalid in new[]{"401","malformed","unknown"}){
        mode=invalid;outbox=new(Path.Combine(dir,invalid+".db"));Record(outbox);var previous=calls;bridge=Bridge(outbox);await bridge.StartAsync();
        await Until(()=>calls>previous);await Task.Delay(150);Check(outbox.ReadPending().Count==1,invalid+" erased a receipt");await bridge.DisposeAsync();
    }
    Console.WriteLine("PASS: unauthorized, malformed and unknown-ID acknowledgements retain SQLite");

    mode="partial";outbox=new(Path.Combine(dir,"partial.db"));Record(outbox);Record(outbox);bridge=Bridge(outbox);await bridge.StartAsync();
    await Until(()=>outbox.ReadPending().Count==1);await bridge.DisposeAsync();
    Console.WriteLine("PASS: partial batch response deletes only the explicitly acknowledged receipt");

    mode="hold";outbox=new(Path.Combine(dir,"cancel.db"));Record(outbox);var before=calls;bridge=Bridge(outbox);await bridge.StartAsync();await Until(()=>calls>before);
    var elapsed=System.Diagnostics.Stopwatch.StartNew();await bridge.DisposeAsync();Check(elapsed.ElapsedMilliseconds<2000,"Shutdown waited for API timeout");Check(outbox.ReadPending().Count==1,"Shutdown lost receipt");
    await Until(()=>cancelled);
    Console.WriteLine("PASS: shutdown cancels an in-flight API request and preserves its receipt");
}finally{await server.StopAsync();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
