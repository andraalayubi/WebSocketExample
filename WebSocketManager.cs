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
        try
        {
            _logger.LogInformation("Raw message received: {Message}", message);

            var msg = JsonSerializer.Deserialize<WebSocketMessage>(message);

            if (msg == null)
            {
                _logger.LogWarning("Failed to deserialize message: {Message}", message);
                return;
            }

            _logger.LogInformation("Deserialized message - Type: {Type}, DocId: {DocId}", msg.type, msg.docId);

            switch (msg.type)
            {
                case "update":
                    await HandleUpdateMessage(documentId, msg.update, webSocket, dbContext);
                    break;
                    
                default:
                    _logger.LogInformation("Unhandled message type: {Type}", msg.type);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON deserialization failed for message: {Message}", message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message: {Message}", message);
        }
    }

    private async Task HandleUpdateMessage(int documentId, object? update, WebSocket webSocket, AppDbContext dbContext)
    {
        try
        {
            // Extract payload dari object
            var updatePayload = ExtractUpdatePayload(update);
            
            if (string.IsNullOrWhiteSpace(updatePayload))
            {
                _logger.LogWarning("Received empty update payload");
                return;
            }

            _logger.LogInformation("Extracted update payload: {Payload}", updatePayload);

            // Coba decode sebagai Base64
            var decodedPayload = TryDecodeBase64String(updatePayload);
            
            string finalPayload;
            if (decodedPayload != null)
            {
                _logger.LogInformation("Successfully decoded Base64. Decoded: {Decoded}", decodedPayload);
                finalPayload = decodedPayload;
            }
            else
            {
                _logger.LogInformation("Using raw payload (not Base64)");
                finalPayload = updatePayload;
            }

            // Update database
            var document = await dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
            if (document != null)
            {
                document.Content = finalPayload;
                document.LastUpdated = DateTime.UtcNow;
                await dbContext.SaveChangesAsync();
                
                _logger.LogInformation("Database updated for document {DocumentId}", documentId);
                
                // Broadcast ke semua client yang terkoneksi ke document ini
                await BroadcastToDocumentClients(documentId, "update", finalPayload);
            }
            else
            {
                _logger.LogWarning("Document {DocumentId} not found", documentId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling update message for document {DocumentId}", documentId);
        }
    }

    private static string? ExtractUpdatePayload(object? update)
    {
        if (update is null)
        {
            return null;
        }

        try
        {
            return update switch
            {
                string text => text,
                JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String => 
                    jsonElement.GetString(),
                JsonElement jsonElement => jsonElement.GetRawText(),
                _ => update.ToString()
            };
        }
        catch (Exception)
        {
            return update.ToString();
        }
    }

    private static string? TryDecodeBase64String(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            // Validasi panjang Base64 (harus kelipatan 4)
            if (value.Length % 4 != 0)
            {
                return null;
            }

            // Validasi karakter Base64
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^[a-zA-Z0-9\+/]*={0,3}$"))
            {
                return null;
            }

            var buffer = Convert.FromBase64String(value);
            var utf8Strict = new UTF8Encoding(false, true);
            var decoded = utf8Strict.GetString(buffer);

            // Cek apakah hasil decode mengandung null character
            if (decoded.IndexOf('\0') >= 0)
            {
                return null;
            }

            return decoded;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task SendToClient(WebSocket webSocket, string type, int docId, object update)
    {
        try
        {
            var message = new WebSocketMessage(type, docId.ToString(), update);
            var messageJson = JsonSerializer.Serialize(message);
            var buffer = Encoding.UTF8.GetBytes(messageJson);
            
            _logger.LogInformation("Sending message to client - Type: {Type}, DocId: {DocId}", type, docId);
            
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.SendAsync(
                    new ArraySegment<byte>(buffer), 
                    WebSocketMessageType.Text, 
                    true, 
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to client");
        }
    }

    private async Task BroadcastToDocumentClients(int docId, string type, object update)
    {
        try
        {
            _logger.LogInformation("Broadcasting to DocId {DocId}. Type: {Type}", docId, type);
            
            var message = new WebSocketMessage(type, docId.ToString(), update);
            var messageJson = JsonSerializer.Serialize(message);
            var buffer = Encoding.UTF8.GetBytes(messageJson);

            var openSockets = _connections
                .Where(kvp => kvp.Value.DocumentId == docId && kvp.Value.Socket.State == WebSocketState.Open)
                .ToList();

            _logger.LogInformation("Broadcasting to {SocketCount} socket(s) for DocId {DocId}", openSockets.Count, docId);

            var tasks = openSockets.Select(async kvp =>
            {
                try
                {
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

            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during broadcast to DocId {DocId}", docId);
        }
    }
}

public sealed record WebSocketMessage(string type, string docId, object? update);