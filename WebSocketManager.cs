using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WebSocketExample.Models;
using System.Text.Json;

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

            // Menangani sinkronisasi pertama kali
            var document = await dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
            if (document == null)
            {
                // Buat dokumen baru
                document = new Document
                {
                    Id = documentId,
                    Content = "Initial content of the new document",
                    LastUpdated = DateTime.UtcNow
                };
                dbContext.Documents.Add(document);
                await dbContext.SaveChangesAsync();

                await SendToClient(webSocket, "sync", documentId, "Document not found. A new document has been created.");
            }
            else
            {
                await SendToClient(webSocket, "sync", documentId, document.Content);
            }

            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await HandleMessage(message, webSocket, dbContext);
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

    private async Task HandleMessage(string message, WebSocket webSocket, AppDbContext dbContext)
    {
        var msg = JsonSerializer.Deserialize<WebSocketMessage>(message);
        if (msg != null)
        {
            switch (msg.Type)
            {
                case "sync":
                    await HandleSyncMessage(msg.DocId, webSocket, dbContext);
                    break;
                case "update":
                    await HandleUpdateMessage(msg.DocId, msg.Update.ToString(), dbContext);
                    break;
            }
        }
    }

    private async Task HandleSyncMessage(int docId, WebSocket webSocket, AppDbContext dbContext)
    {
        var document = await dbContext.Documents.FirstOrDefaultAsync(d => d.Id == docId);
        if (document == null)
        {
            // Jika dokumen belum ada, buat dokumen baru
            document = new Document
            {
                Id = docId,
                Content = "Initial content",
                LastUpdated = DateTime.UtcNow
            };
            dbContext.Documents.Add(document);
            await dbContext.SaveChangesAsync();
            await SendToClient(webSocket, "sync", docId, $"Document with ID {docId} is created.");
        }
        else
        {
            // Kirim konten awal ke klien
            await SendToClient(webSocket, "sync", docId, document.Content);
        }
    }

    private async Task HandleUpdateMessage(int docId, string update, AppDbContext dbContext)
    {
        var document = await dbContext.Documents.FirstOrDefaultAsync(d => d.Id == docId);
        if (document != null)
        {
            // Update konten dan LastUpdated
            document.Content = update;
            document.LastUpdated = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();
            await BroadcastToAll("update", docId, update);
        }
    }

    private async Task SendToClient(WebSocket webSocket, string type, int docId, object update)
    {
        var message = new WebSocketMessage
        {
            Type = type,
            DocId = docId,
            Update = update
        };

        var messageJson = JsonSerializer.Serialize(message);
        var buffer = Encoding.UTF8.GetBytes(messageJson);
        await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task BroadcastToAll(string type, int docId, object update)
    {
        var message = new WebSocketMessage
        {
            Type = type,
            DocId = docId,
            Update = update
        };

        var messageJson = JsonSerializer.Serialize(message);
        var buffer = Encoding.UTF8.GetBytes(messageJson);

        var tasks = _sockets.Values
            .Where(socket => socket.State == WebSocketState.Open)
            .Select(socket => socket.SendAsync(
                new ArraySegment<byte>(buffer),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None));

        await Task.WhenAll(tasks);
    }
}

public class WebSocketMessage
{
    public string Type { get; set; }
    public int DocId { get; set; }
    public object Update { get; set; }
}
