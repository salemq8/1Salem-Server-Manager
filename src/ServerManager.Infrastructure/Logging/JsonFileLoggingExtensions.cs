using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ServerManager.Infrastructure.Logging;

public static class JsonFileLoggingExtensions
{
    public static ILoggingBuilder AddJsonFile(
        this ILoggingBuilder builder,
        JsonFileLoggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        builder.Services.AddSingleton<ILoggerProvider>(new JsonFileLoggerProvider(options));
        return builder;
    }
}
