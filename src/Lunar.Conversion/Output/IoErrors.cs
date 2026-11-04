using Lunar.Domain.Shared;

namespace Lunar.Conversion.Output;

/// <summary>
/// How a file-system exception becomes an expected, typed failure with a message the user can act on (no path, no exception text).
/// One place, so the output step and the providers say the same thing about a full disk or a folder that cannot be written.
/// </summary>
internal static class IoErrors
{
    public const string DiskFullMessage = "There is not enough space on the disk to save the result. Free some space and try again; your original file is untouched.";

    public static bool IsIoProblem(Exception ex) => ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException;

    public static OperationError Map(Exception ex) => ex switch
    {
        IOException io when IsDiskFull(io) => new OperationError(ErrorCode.IoFailure, DiskFullMessage, retryable: true),
        UnauthorizedAccessException => new OperationError(ErrorCode.AccessDenied, "Lunar can't save to that folder. Check that you may write there, or choose another folder."),
        PathTooLongException => new OperationError(ErrorCode.IoFailure, "The path for the result is too long. Choose a folder closer to the top of a drive."),
        DirectoryNotFoundException => new OperationError(ErrorCode.IoFailure, "The folder for the results is no longer available. Choose another folder and try again.", retryable: true),
        _ => new OperationError(ErrorCode.IoFailure, "The result couldn't be saved. Check the folder and try again; your original file is untouched.", retryable: true)
    };

    // ERROR_HANDLE_DISK_FULL (39) and ERROR_DISK_FULL (112), as the low word of the HRESULT.
    private static bool IsDiskFull(IOException io) => (io.HResult & 0xFFFF) is 39 or 112;
}
