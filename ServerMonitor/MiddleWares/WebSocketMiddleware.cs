using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

public class WebSocketMiddleware
{
    private static readonly List<WebSocket> _sockets = new List<WebSocket>();
    private readonly RequestDelegate _next;

    public WebSocketMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path == "/ws")
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var socket = await context.WebSockets.AcceptWebSocketAsync();
                _sockets.Add(socket);

                await HandleWebSocket(socket);
            }
            else
            {
                context.Response.StatusCode = 400; // Bad Request
            }
        }
        else
        {
            await _next(context);
        }
    }

    private async Task HandleWebSocket(WebSocket socket)
    {
        var buffer = new byte[1024 * 4];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                _sockets.Remove(socket);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
            }
        }
    }
    public static async Task BroadcastMessageAsync(string message)
    {
        Console.WriteLine($"Attempting to send message: {message}");
        var socketsToRemove = new List<WebSocket>();
        foreach (var socket in _sockets.ToList())
        {
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    var messageBytes = Encoding.UTF8.GetBytes(message);
                    await socket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    Console.WriteLine("Message sent: " + message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error sending message to socket: " + ex.Message);
                    socketsToRemove.Add(socket);
                }
            }
            else
            {
                Console.WriteLine("WebSocket is not connected.");
                socketsToRemove.Add(socket);
            }
        }
        // Remove closed or errored sockets from the list
        foreach (var socket in socketsToRemove)
        {
            _sockets.Remove(socket);
        }
    }
}
