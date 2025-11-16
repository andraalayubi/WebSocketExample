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

builder.Services.AddSignalR();

builder.Services.AddScoped<DocumentHub>();

var app = builder.Build();

app.UseCors("AllowAll");


app.MapHub<DocumentHub>("/ws/document");

app.Run();