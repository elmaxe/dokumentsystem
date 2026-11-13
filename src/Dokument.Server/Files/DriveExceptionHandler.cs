using Dokument.Application.Files;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dokument.Server.Files;

/// <summary>Turns a refused file operation into a problem response, with a machine-readable <c>code</c>.</summary>
public sealed class DriveExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken ct
    )
    {
        if (exception is not DriveException drive)
        {
            return false;
        }

        var (status, title) = drive.Error switch
        {
            DriveError.NotFound => (StatusCodes.Status404NotFound, "Not found"),
            DriveError.Forbidden => (StatusCodes.Status403Forbidden, "Not allowed"),
            DriveError.NameTaken => (StatusCodes.Status409Conflict, "Name already taken"),
            DriveError.QuotaExceeded => (
                StatusCodes.Status413PayloadTooLarge,
                "Not enough storage"
            ),
            DriveError.UploadMismatch => (StatusCodes.Status409Conflict, "Upload conflict"),
            DriveError.UploadBusy => (StatusCodes.Status423Locked, "Upload busy"),
            DriveError.LinkGone => (StatusCodes.Status404NotFound, "This link no longer works"),
            DriveError.PasswordRequired => (
                StatusCodes.Status401Unauthorized,
                "This link needs a password"
            ),
            DriveError.WrongPassword => (StatusCodes.Status401Unauthorized, "Wrong password"),
            _ => (StatusCodes.Status400BadRequest, "Invalid request"),
        };

        if (context.Request.Path.StartsWithSegments("/api/v1/uploads"))
        {
            context.Response.Headers["Tus-Resumable"] = TusEndpoints.Version;
        }

        context.Response.StatusCode = status;
        var details = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = drive.Message,
        };
        details.Extensions["code"] = JsonNamingPolicyCamel(drive.Error.ToString());
        if (drive.ConflictingNodeId is { } conflicting)
        {
            details.Extensions["conflictingNodeId"] = conflicting;
        }

        return await problems.TryWriteAsync(
            new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = details,
                Exception = exception,
            }
        );
    }

    private static string JsonNamingPolicyCamel(string value) =>
        char.ToLowerInvariant(value[0]) + value[1..];
}
