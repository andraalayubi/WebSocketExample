using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebSocketExample.Models;
using System.Text.Json;

public class WebSocketManager
{
    private readonly ConcurrentDictionary<string, ClientConnection> _connections = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WebSocketManager> _logger;

    public WebSocketManager(IServiceProvider serviceProvider, ILogger<WebSocketManager> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public sealed record ClientConnection(WebSocket Socket, int DocumentId);

    public async Task HandleConnection(WebSocket webSocket, int documentId)
    {
        var socketId = Guid.NewGuid().ToString();
        var connection = new ClientConnection(webSocket, documentId);
        _connections.TryAdd(socketId, connection);

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
                    await HandleMessage(documentId, message, webSocket, dbContext);
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        finally
        {
            _connections.TryRemove(socketId, out _);
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            webSocket.Dispose();
        }
    }

    private async Task HandleMessage(int documentId, string message, WebSocket webSocket, AppDbContext dbContext)
    {
        var msg = JsonSerializer.Deserialize<WebSocketMessage>(message);

        if (msg == null)
        {
            _logger.LogInformation("Received message: {Message}", message);
            return;
        }

        switch (msg.Type)
        {
            case "update":
                var updatePayload = ExtractUpdatePayload(msg.Update);
                var decodedPayload = TryDecodeBase64String(updatePayload);

                if (decodedPayload != null)
                {
                    updatePayload = decodedPayload;
                }
                else if (!string.IsNullOrWhiteSpace(updatePayload))
                {
                    _logger.LogWarning("Failed to decode update payload from Base64. Raw payload: {RawPayload}", updatePayload);
                }

                if (string.IsNullOrWhiteSpace(updatePayload))
                {
                    _logger.LogWarning("Received update message without a valid payload. Original message: {Message}", message);
                    break;
                }

                _logger.LogInformation("Received update message. Decoded payload: {DecodedPayload}", updatePayload);
                await HandleUpdateMessage(documentId, updatePayload, dbContext);
                break;
            default:
                _logger.LogInformation("Received message: {Message}", message);
                break;
        }
    }

    private static string? ExtractUpdatePayload(object? update)
    {
        if (update is null)
        {
            return null;
        }

        return update switch
        {
            string text => text,
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String => jsonElement.GetString(),
            JsonElement jsonElement => jsonElement.GetRawText(),
            _ => update.ToString()
        };
    }

    private static string? TryDecodeBase64String(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var buffer = Convert.FromBase64String(value);
            return Encoding.UTF8.GetString(buffer);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task HandleUpdateMessage(int docId, string update, AppDbContext dbContext)
    {
        if (string.IsNullOrWhiteSpace(update))
        {
            _logger.LogWarning("Skipping update for DocId {DocId} because the payload was empty.", docId);
            return;
        }

        var document = await dbContext.Documents.FirstOrDefaultAsync(d => d.Id == docId);
        if (document != null)
        {
            // Update konten dan LastUpdated
            document.Content = update;
            document.LastUpdated = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();
            await BroadcastToDocumentClients(docId, "update", update); // Broadcast only to the clients of this document
        }
    }

    private async Task SendToClient(WebSocket webSocket, string type, int docId, object update)
    {
        var message = new WebSocketMessage(type, docId, update);

        var messageJson = JsonSerializer.Serialize(message);
        var buffer = Encoding.UTF8.GetBytes(messageJson);
        _logger.LogInformation("Sending message to socket {SocketState}: {MessageJson}", webSocket.State, messageJson);
        await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    // Modify the Broadcast method to send only to clients subscribed to the same DocId
    private async Task BroadcastToDocumentClients(int docId, string type, object update)
    {
        _logger.LogInformation("Broadcasting to DocId {DocId}. Type: {Type}, Update: {Update}", docId, type, update);
        var message = new WebSocketMessage(type, docId, update);

        var messageJson = JsonSerializer.Serialize(message);
        var buffer = Encoding.UTF8.GetBytes(messageJson);

        var openSockets = _connections
            .Where(kvp => kvp.Value.DocumentId == docId && kvp.Value.Socket.State == WebSocketState.Open)
            .ToList();

        _logger.LogInformation("Broadcasting payload to {SocketCount} socket(s).", openSockets.Count);

        var tasks = openSockets.Select(async kvp =>
        {
            try
            {
                // Check if the socket is subscribed to the same DocId
                // (You can manage a mapping of socketId to docId if needed)
                await kvp.Value.Socket.SendAsync(
                    new ArraySegment<byte>(buffer),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Broadcast failed for socket {SocketId}", kvp.Key);
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "One or more broadcast operations threw an exception.");
        }
    }
}

public sealed record WebSocketMessage(string Type, int DocId, object Update);
