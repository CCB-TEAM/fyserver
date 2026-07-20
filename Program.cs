using fyserver;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi


config.appconfig.read();
 new http().StartHttpServer();
await Task.Delay(2000);
new ws().StartWsServerAsync();


//ClientWebSocket webSocket = new ClientWebSocket();
//await webSocket.ConnectAsync(new Uri(config.appconfig.getAddressWs2()), CancellationToken.None);
//string message = JsonSerializer.Serialize(new WebSocketMessage(Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(), Message: "Fuck!", Channel: "ping",Context:null), GameConstants.JsonOptions);
//ws.SendAsync(webSocket, message);
//var buffer = new byte[1024];
//List<byte> bs = new List<byte>();
//WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
////文本消息
//bs.AddRange(buffer.Take(result.Count));
////消息是否已接收完全
//if (result.EndOfMessage)
//{
//    //发送过来的消息
//    string userMsg = Encoding.UTF8.GetString(bs.ToArray(), 0, bs.Count);
//    Console.WriteLine(userMsg);
//    //清空消息容器
//    bs = new List<byte>();
//}
//await webSocket.CloseAsync(WebSocketCloseStatus.Empty, "", CancellationToken.None);
Command.StartCommandLoop();
//var d = "%%24|060d0n1d1g1G1H1r1t1v7N7Y8U9g9n9qdDdEdze4efgsgtgvhThWjBjqowrht5tUtWw1w3wrwx;1b;;~;;;|0N1b";
//d = d.Remove(0, 5);
//var cards = new List<MatchCard>();
//int cCount = 0;
//int startId=1;
//a.InitLibrary("./deckCodeIDsTable2.json", "");
