using Microsoft.AspNetCore.Mvc;
using System.Net.Mime;

namespace PocketSpaceServer.Utility
{
    public class FileCallbackResult : FileResult
    {
        private readonly Func<Stream, ActionContext, Task> _callback;

        public FileCallbackResult(string contentType, Func<Stream, ActionContext, Task> callback)
            : base(contentType)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        public override async Task ExecuteResultAsync(ActionContext context)
        {
            var response = context.HttpContext.Response;
            response.ContentType = ContentType;
            if (!string.IsNullOrEmpty(FileDownloadName))
            {
                response.Headers.Add("Content-Disposition", $"attachment; filename=\"{FileDownloadName}\"");
            }

            await _callback(response.Body, context);
        }
    }

}
