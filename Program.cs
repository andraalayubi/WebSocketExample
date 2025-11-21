using Microsoft.EntityFrameworkCore;
using WebSocketExample;
using WebSocketExample.Logging;
using WebSocketExample.Models;
using System.IO;

var builder = WebApplication.CreateBuilder(args);

var logFilePath = Path.Combine(builder.Environment.ContentRootPath, "Logs", "app.log");
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "Logs"));
builder.Logging.AddFile(logFilePath);

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
    options.EnableSensitiveDataLogging();
});

// Configure CORS for ngrok
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.WithOrigins(
                  "http://localhost:3000",
                  "https://localhost:3000")
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// Add SignalR
builder.Services.AddSignalR();

var app = builder.Build();

app.UseCors("AllowAll");

app.MapHub<DocumentHub>("/documentHub");

app.Run();