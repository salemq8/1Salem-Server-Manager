using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Agent;

public sealed class ApiExceptionMiddleware(
    RequestDelegate next,
    ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Agent request {Method} {Path} failed.",
                context.Request.Method,
                context.Request.Path);
            if (context.Response.HasStarted)
            {
                throw;
            }

            var (status, code, reason, fix, retry) = Classify(exception);
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(
                new ApiErrorResponse(
                    code,
                    exception.Message,
                    reason,
                    $"{context.Request.Method} {context.Request.Path}",
                    fix,
                    retry));
        }
    }

    private static (
        int Status,
        string Code,
        string Reason,
        string Fix,
        bool Retry) Classify(Exception exception) =>
        exception switch
        {
            GameServerOrchestrator.ServerBudgetExceededException => (
                StatusCodes.Status409Conflict,
                "UnsafeServerBudget",
                "The current memory policy does not have enough safe capacity for this start.",
                "Open Palworld Performance, recalculate, save valid values, or use Start Anyway Once when explicitly enabled.",
                true),
            ServerBusyException => (
                StatusCodes.Status409Conflict,
                "ServerBusy",
                "Another operation is already in progress for this server.",
                "Wait for the in-progress operation to finish, then retry.",
                true),
            KeyNotFoundException => (
                StatusCodes.Status404NotFound,
                "NotFound",
                "The requested registered item does not exist.",
                "Refresh the dashboard and select an existing server or operation.",
                false),
            UnauthorizedAccessException => (
                StatusCodes.Status403Forbidden,
                "FolderPermission",
                "The Agent account cannot access the requested path or operation.",
                "Choose an Agent-writable path or repair the folder permissions.",
                true),
            ArgumentException => (
                StatusCodes.Status400BadRequest,
                "InvalidRequest",
                "One or more submitted values are invalid.",
                "Review the highlighted settings and submit valid values.",
                true),
            FileNotFoundException => (
                StatusCodes.Status409Conflict,
                "RequiredFileMissing",
                "A required executable or managed server file is missing.",
                "Repair the server files or choose the correct executable path.",
                true),
            IOException => (
                StatusCodes.Status409Conflict,
                "FileOrPortConflict",
                "A file, folder, process, or port conflicts with the operation.",
                "Resolve the reported conflict, then retry.",
                true),
            HttpRequestException => (
                StatusCodes.Status502BadGateway,
                "DownloadFailure",
                "An official download or metadata request failed.",
                "Check internet access and retry the verified download.",
                true),
            _ => (
                StatusCodes.Status500InternalServerError,
                "AgentOperationFailed",
                "The Agent could not complete the requested operation.",
                "Open Logs or copy diagnostics, correct the reported failure, and retry.",
                true)
        };
}
