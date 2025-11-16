using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text;
using WebSocketExample.Models;

public class DocumentHub : Hub
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<DocumentHub> _logger;

    public DocumentHub(AppDbContext dbContext, ILogger<DocumentHub> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task JoinDocument(int documentId)
    {
        if (documentId <= 0)
        {
            await Clients.Caller.SendAsync("ReceiveError", "DocumentId must be a positive integer.");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, DocumentGroupName(documentId));

        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
        if (document == null)
        {
            document = new Document
            {
                Id = documentId,
                Content = "Initial content of the new document",
                LastUpdated = DateTime.UtcNow
            };

            _dbContext.Documents.Add(document);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Created new document {DocumentId} for connection {ConnectionId}.", documentId, Context.ConnectionId);
        }

        await Clients.Caller.SendAsync("ReceiveSync", documentId, document.Content);
    }

    public async Task UpdateDocument(int documentId, string? payload)
    {
        if (documentId <= 0)
        {
            await Clients.Caller.SendAsync("ReceiveError", "DocumentId must be a positive integer.");
            return;
        }

        var updatePayload = NormalizePayload(payload);
        if (string.IsNullOrWhiteSpace(updatePayload))
        {
            _logger.LogWarning("Skipping empty update from connection {ConnectionId} for document {DocumentId}.", Context.ConnectionId, documentId);
            return;
        }

        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
        if (document == null)
        {
            document = new Document
            {
                Id = documentId,
                Content = updatePayload,
                LastUpdated = DateTime.UtcNow
            };

            _dbContext.Documents.Add(document);
        }
        else
        {
            document.Content = updatePayload;
            document.LastUpdated = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync();

        await Clients.Group(DocumentGroupName(documentId))
            .SendAsync("ReceiveUpdate", documentId, updatePayload);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }

    private static string DocumentGroupName(int documentId) => $"document-{documentId}";

    private static string? NormalizePayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        var decoded = TryDecodeBase64String(payload);
        return string.IsNullOrWhiteSpace(decoded) ? payload : decoded;
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
}
