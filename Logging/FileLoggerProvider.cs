using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace WebSocketExample.Logging
{
    public static class FileLoggerExtensions
    {
        public static ILoggingBuilder AddFile(this ILoggingBuilder builder, string filePath)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path must be provided", nameof(filePath));
            }

            builder.Services.AddSingleton<ILoggerProvider>(_ => new FileLoggerProvider(filePath));
            return builder;
        }
    }

    internal sealed class FileLoggerProvider : ILoggerProvider
    {
        private readonly string _filePath;
        private readonly object _lock = new();
        private StreamWriter? _writer;
        private bool _disposed;

        public FileLoggerProvider(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            _writer = CreateStreamWriter(_filePath);
        }

        public ILogger CreateLogger(string categoryName)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FileLoggerProvider));
            }

            if (_writer == null)
            {
                throw new InvalidOperationException("StreamWriter is not initialized.");
            }

            return new FileLogger(categoryName, _writer, _lock);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            lock (_lock)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }

        private static StreamWriter CreateStreamWriter(string filePath)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var fileStream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            return new StreamWriter(fileStream)
            {
                AutoFlush = true
            };
        }

        private sealed class FileLogger : ILogger
        {
            private readonly string _categoryName;
            private readonly StreamWriter _writer;
            private readonly object _lock;

            public FileLogger(string categoryName, StreamWriter writer, object @lock)
            {
                _categoryName = categoryName;
                _writer = writer;
                _lock = @lock;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                if (formatter == null)
                {
                    throw new ArgumentNullException(nameof(formatter));
                }

                var message = formatter(state, exception);
                if (string.IsNullOrWhiteSpace(message) && exception == null)
                {
                    return;
                }

                var logLine = $"{DateTimeOffset.UtcNow:o} [{logLevel}] {_categoryName}: {message}";
                if (exception != null)
                {
                    logLine += Environment.NewLine + exception;
                }

                lock (_lock)
                {
                    _writer.WriteLine(logLine);
                }
            }
        }
    }
}
