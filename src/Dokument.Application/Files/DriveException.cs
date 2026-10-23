namespace Dokument.Application.Files;

/// <summary>A file operation the user asked for that can't be done. The server turns it into a problem response.</summary>
public sealed class DriveException(DriveError error, string message, Guid? conflictingNodeId = null)
    : Exception(message)
{
    public DriveError Error { get; } = error;

    /// <summary>For <see cref="DriveError.NameTaken"/>: the node that already has the name.</summary>
    public Guid? ConflictingNodeId { get; } = conflictingNodeId;

    public static DriveException NotFound(string what = "file or folder") =>
        new(DriveError.NotFound, $"The {what} doesn't exist, or you don't have access to it.");
}

public enum DriveError
{
    NotFound,
    InvalidRequest,
    NameTaken,
    QuotaExceeded,

    /// <summary>The bytes didn't match the hash the client sent, or the upload is in a state that refuses the request.</summary>
    UploadMismatch,
    UploadBusy,
}
