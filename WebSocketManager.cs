using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WebSocketExample.Models;

public class WebSocketManager
{
    private readonly ConcurrentDictionary<string, WebSocket> _sockets = new();
    private readonly IServiceProvider _serviceProvider;

    public WebSocketManager(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task HandleConnection(WebSocket webSocket, int documentId)
    {
        var socketId = Guid.NewGuid().ToString();
        _sockets.TryAdd(socketId, webSocket);

        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var buffer = new byte[1024 * 4];

            var document = await dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
            if (document == null)
            {
                // Buat dokumen baru
                document = new Document
                {
                    Id = documentId,
                    Title = "New Document",
                    Content = "Initial content of the new document",
                    LastUpdated = DateTime.UtcNow
                };
                dbContext.Documents.Add(document);
                await dbContext.SaveChangesAsync();

                await SendToClient(webSocket, "Document not found. A new document has been created.");
            }
            else
            {
                var initialContent = $"Initial Content: {document.Content}";
                await SendToClient(webSocket, initialContent);
            }

            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await ProcessMessage(message, documentId, dbContext);
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        finally
        {
            _sockets.TryRemove(socketId, out _);
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            webSocket.Dispose();
        }
    }

    private async Task ProcessMessage(string message, int documentId, AppDbContext dbContext)
    {
        // Update database
        var document = await dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
        if (document != null)
        {
            document.Content = message;
            document.LastUpdated = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();
        }

        await BroadcastToAll($"Updated: {message}");
    }

    private async Task BroadcastToAll(string message)
    {
        var buffer = Encoding.UTF8.GetBytes(message);
        var tasks = _sockets.Values
            .Where(socket => socket.State == WebSocketState.Open)
            .Select(socket => socket.SendAsync(
                new ArraySegment<byte>(buffer),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None));

        await Task.WhenAll(tasks);
    }

    private async Task SendToClient(WebSocket webSocket, string message)
    {
        Console.WriteLine($"Sending message to client: {message}");
        var buffer = Encoding.UTF8.GetBytes(message);
        await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
    }
}
