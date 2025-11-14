using Microsoft.EntityFrameworkCore;
using WebSocketExample.Logging;
using WebSocketExample.Models;
using System.IO;

var builder = WebApplication.CreateBuilder(args);

var logFilePath = Path.Combine(builder.Environment.ContentRootPath, "Logs", "app.log");
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "Logs"));
builder.Logging.AddFile(logFilePath);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
);

// Configure CORS for ngrok
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Register WebSocketManager
builder.Services.AddSingleton<WebSocketManager>();

var app = builder.Build();

app.UseCors("AllowAll");

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