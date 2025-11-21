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
            var updatePayload = ExtractUpdatePayload(msg.update);

            if (string.IsNullOrWhiteSpace(updatePayload))
            {
                _logger.LogWarning("Received empty update payload");
                return;
            }

            var decodedPayload = TryDecodeBase64String(updatePayload);

            _logger.LogInformation("Raw Y.js update bytes: {Bytes}", updatePayload);
            _logger.LogInformation("Raw Y.js update bytes: {Bytes}", decodedPayload);

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

            // Update database - SIMPAN binary update, jangan convert ke base64
            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var document = await dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
            if (document != null)
            {
                document.Content = finalPayload;
                document.YjsState = [];
                document.LastUpdated = DateTime.UtcNow;

                await dbContext.SaveChangesAsync();

                _logger.LogInformation("Database updated for document {DocumentId} by {ConnectionId}", documentId, Context.ConnectionId);

                // Broadcast binary update asli ke clients lain
                await BroadcastToDocumentClients(documentId, "update", finalPayload);
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

            var message = new SignalRMessage(type, docId.ToString(), update.ToString());

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
}

public sealed record SignalRMessage(string type, string docId, string? update);