using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace PocketSpaceServer.Storage;

public sealed class StorageErrorsAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is ArgumentException)
            context.Result = new BadRequestObjectResult(new { message = "Invalid file name or storage path." });
        else if (context.Exception is FileNotFoundException or DirectoryNotFoundException)
            context.Result = new NotFoundObjectResult(new { message = "File or folder not found." });
        else if (context.Exception is IOException or UnauthorizedAccessException)
            context.Result = new ConflictObjectResult(new { message = "The file operation could not be completed. Try again." });
        else if (context.Exception is Amazon.S3.AmazonS3Exception)
            context.Result = new ObjectResult(new { message = "S3 storage is unavailable. Check the bucket and credentials." })
            { StatusCode = StatusCodes.Status503ServiceUnavailable };
        else return;
        context.ExceptionHandled = true;
    }
}
