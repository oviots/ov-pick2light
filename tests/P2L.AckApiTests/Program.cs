using System.Net;
using System.Text.Json;
using P2L.Mqtt;
using P2L.Mqtt.Configuration;
static void Check(bool value,string message) {if(!value)throw new Exception(message);}
using var legacy=JsonDocument.Parse("{}");
Check(!AckApiSettings.FromRemote(legacy.RootElement).Enabled,"Legacy config enabled ACK");
using var remote=JsonDocument.Parse("""{"ackApi":{"enabled":true,"url":"https://gateway.example/ack","token":"test-ack-token-0123456789abcdef012345"}}""");
var settings=AckApiSettings.FromRemote(remote.RootElement);
var handler=new Recorder();
using var client=new AckApiClient(settings,handler);
await Task.Delay(100);
Check(handler.Calls==0,"ACK client sent automatically");
using var payload=JsonDocument.Parse("""{"action":"test"}""");
await client.SendAsync(payload.RootElement);
Check(handler.Calls==1&&handler.Auth=="Bearer "+settings.Token&&handler.Body==payload.RootElement.GetRawText(),"Explicit ACK request changed payload or auth");
using var disabled=new AckApiClient(new(),new Recorder());
try{await disabled.SendAsync(payload.RootElement);throw new Exception("Disabled ACK sent");}catch(InvalidOperationException){}
handler.Status=HttpStatusCode.BadRequest;
try{await client.SendAsync(payload.RootElement);throw new Exception("ACK failure hidden");}catch(HttpRequestException){}
Check(handler.Calls==2,"ACK retried without caller instruction");
foreach(var url in new[]{"http://gateway.example/ack","https://user:pass@gateway.example/ack","https://gateway.example/ack?token=x"}) {
 try{using var invalid=new AckApiClient(new(){Enabled=true,Url=url,Token=settings.Token});throw new Exception("Invalid URL accepted");}catch(InvalidDataException){}
}
Console.WriteLine("PASS ACK optional config, explicit-only send, Bearer auth, disabled guard, validation and HTTP failure; no automatic retry");
sealed class Recorder:HttpMessageHandler {
 public int Calls;public string? Auth,Body;public HttpStatusCode Status=HttpStatusCode.OK;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
  Calls++;Auth=request.Headers.Authorization?.ToString();Body=await request.Content!.ReadAsStringAsync(ct);
  return new HttpResponseMessage(Status);
 }
}
