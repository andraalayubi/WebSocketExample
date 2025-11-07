using Microsoft.EntityFrameworkCore;
using WebSocketExample.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
);

// Register WebSocketManager
builder.Services.AddSingleton<WebSocketManager>();

var app = builder.Build();

app.UseWebSockets();

app.Map("/ws/{documentId}", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var documentIdValue = context.Request.RouteValues["documentId"]?.ToString();
        if (!int.TryParse(documentIdValue, out var documentId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Valid numeric document ID is required.");
            return;
        }

        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        var wsManager = context.RequestServices.GetRequiredService<WebSocketManager>();
        await wsManager.HandleConnection(webSocket, documentId);
    }
    else
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }
});



app.Run();