using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using WebSocketExample.Models;

namespace WebSocketExample;

public class DocumentHub : Hub
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DocumentHub> _logger;
    private static readonly ConcurrentDictionary<string, string> _connectionDocumentMap = new();

    public DocumentHub(IServiceProvider serviceProvider, ILogger<DocumentHub> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        var documentIdValue = httpContext?.Request.Query["documentId"].ToString();

        if (!int.TryParse(documentIdValue, out var documentId))
        {
            _logger.LogWarning("Invalid document ID from connection {ConnectionId}", Context.ConnectionId);
            Context.Abort();
            return;
        }

        _connectionDocumentMap[Context.ConnectionId] = documentId.ToString();

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Handle initial synchronization
        var document = await dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
        _logger.LogInformation("Database content: {Content}", document?.Content);
        if (document == null)
        {
            // Create new document placeholder for Yjs state (stored as Base64 string)
            document = new Document
            {
                Id = documentId,
                Content = string.Empty,
                LastUpdated = DateTime.UtcNow
            };
            dbContext.Documents.Add(document);
            await dbContext.SaveChangesAsync();

            await Clients.Caller.SendAsync("Sync", new SignalRMessage("sync", documentId.ToString(), string.Empty));
        }
        else
        {
            await Clients.Caller.SendAsync("Sync", new SignalRMessage("sync", documentId.ToString(), document.Content));
        }

        _logger.LogInformation("Client {ConnectionId} connected to document {DocumentId}", Context.ConnectionId, documentId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _connectionDocumentMap.TryRemove(Context.ConnectionId, out _);
        _logger.LogInformation("Client {ConnectionId} disconnected", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task HandleUpdate(string message)
    {
        try
        {
            _logger.LogInformation("Raw message received from {ConnectionId}: {Message}", Context.ConnectionId, message);

            var msg = JsonSerializer.Deserialize<SignalRMessage>(message);

            if (msg == null)
            {
                _logger.LogWarning("Failed to deserialize message from {ConnectionId}: {Message}", Context.ConnectionId, message);
                return;
            }

            if (msg.type == "update")
            {
                await HandleUpdateMessage(msg);
            }
            else
            {
                _logger.LogInformation("Unhandled message type from {ConnectionId}: {Type}", Context.ConnectionId, msg.type);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON deserialization failed for message from {ConnectionId}: {Message}", Context.ConnectionId, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message from {ConnectionId}: {Message}", Context.ConnectionId, message);
        }
    }

    private async Task HandleUpdateMessage(SignalRMessage msg)
    {
        if (!int.TryParse(msg.docId, out var documentId))
        {
            _logger.LogWarning("Invalid document ID in update message from {ConnectionId}", Context.ConnectionId);
            return;
        }

        try
        {
            var updateBytes = ExtractUpdateBytes(msg.update);

            if (updateBytes is null || updateBytes.Length == 0)
            {
                _logger.LogWarning("Received empty or invalid update payload from {ConnectionId}", Context.ConnectionId);
                return;
            }

            _logger.LogInformation("Raw Y.js update bytes: {Bytes}", Convert.ToBase64String(updateBytes));
            _logger.LogInformation("Update length: {Length}", Encoding.UTF8.GetString(updateBytes));

            // Update database - SIMPAN binary update, jangan convert ke base64
            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var document = await dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
            if (document != null)
            {
                document.Content = Convert.ToBase64String(updateBytes);
                document.YjsState = updateBytes;
                document.LastUpdated = DateTime.UtcNow;

                await dbContext.SaveChangesAsync();

                _logger.LogInformation("Database updated for document {DocumentId} by {ConnectionId}", documentId, Context.ConnectionId);

                // Broadcast binary update asli ke clients lain
                await BroadcastToDocumentClients(documentId, "update", updateBytes);
            }
            else
            {
                _logger.LogWarning("Document {DocumentId} not found for update from {ConnectionId}", documentId, Context.ConnectionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling update message for document {DocumentId} from {ConnectionId}", documentId, Context.ConnectionId);
        }
    }

    private async Task BroadcastToDocumentClients(int docId, string type, object update)
    {
        try
        {
            _logger.LogInformation("Broadcasting to DocId {DocId}. Type: {Type}", docId, type);

            var message = new SignalRMessage(type, docId.ToString(), update);

            var connectionIds = _connectionDocumentMap
                .Where(kvp => kvp.Value == docId.ToString())
                .Select(kvp => kvp.Key)
                .ToList();

            _logger.LogInformation("Broadcasting to {ConnectionCount} connection(s) for DocId {DocId}", connectionIds.Count, docId);

            if (connectionIds.Any())
            {
                await Clients.Clients(connectionIds).SendAsync("ReceiveUpdate", message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during broadcast to DocId {DocId}", docId);
        }
    }

    private static byte[]? ExtractUpdateBytes(object? update)
    {
        if (update is null)
        {
            return null;
        }

        try
        {
            // Handle JSON array dari frontend Y.js
            if (update is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
            {
                return DecodeNumericArray(jsonElement);
            }

            // Handle jika sudah berupa List<int> atau array
            if (update is IEnumerable<int> intEnumerable)
            {
                return intEnumerable.Select(value =>
                {
                    if (value < 0 || value > 255)
                    {
                        throw new ArgumentOutOfRangeException($"Value {value} is out of byte range");
                    }
                    return (byte)value;
                }).ToArray();
            }

            // Handle string (fallback - seharusnya tidak terjadi dengan Y.js)
            if (update is string stringValue)
            {
                // Coba parse sebagai JSON array dulu
                try
                {
                    var element = JsonSerializer.Deserialize<JsonElement>(stringValue);
                    if (element.ValueKind == JsonValueKind.Array)
                    {
                        return DecodeNumericArray(element);
                    }
                }
                catch
                {
                    // Jika bukan JSON, coba sebagai base64
                    return Convert.FromBase64String(stringValue);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error extracting update bytes: {ex.Message}");
            return null;
        }
    }

    private static byte[] DecodeNumericArray(JsonElement arrayElement)
    {
        var buffer = new List<byte>();

        foreach (var element in arrayElement.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var intValue))
            {
                if (intValue >= 0 && intValue <= 255)
                {
                    buffer.Add((byte)intValue);
                }
                else
                {
                    throw new ArgumentOutOfRangeException($"Array value {intValue} is out of byte range");
                }
            }
            else
            {
                throw new InvalidOperationException("Array contains non-numeric elements");
            }
        }

        return buffer.ToArray();
    }
}

public sealed record SignalRMessage(string type, string docId, object? update);