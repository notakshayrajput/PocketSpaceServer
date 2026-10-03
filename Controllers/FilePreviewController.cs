using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Controllers;

[ApiController]
[Route("api/space/preview")]
[StorageErrors]
public sealed class FilePreviewController(UserStorage storage, StorageManager stores) : ControllerBase
{
    private const long MaxVisualBytes = 32L * 1024 * 1024;
    private const long MaxTextBytes = 2L * 1024 * 1024;
    private const long MaxDocumentBytes = 12L * 1024 * 1024;
    private const int MaxPreviewCharacters = 120_000;
    private static readonly IReadOnlyDictionary<string, string> VisualTypes = new Dictionary<string, string>
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".bmp"] = "image/bmp",
        [".avif"] = "image/avif", [".pdf"] = "application/pdf"
    };
    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".log", ".json", ".xml",
        ".yaml", ".yml", ".ini", ".html", ".htm", ".css", ".js", ".ts",
        ".jsx", ".tsx", ".rtf"
    };

    private async Task<(IStorageBackend Backend, string Path, StorageItem? Item)> LocateAsync(string relativePath)
    {
        var backend = await stores.CurrentAsync();
        var path = storage.Resolve(User, relativePath);
        return (backend, path, await backend.StatAsync(path, HttpContext.RequestAborted));
    }

    [HttpGet("content")]
    public async Task<IActionResult> Visual([FromQuery] string path)
    {
        var (backend, fullPath, item) = await LocateAsync(path);
        if (item is null || item.IsFolder) return NotFound(new { message = "File not found." });
        if (!VisualTypes.TryGetValue(Path.GetExtension(item.Name).ToLowerInvariant(), out var contentType))
            return StatusCode(StatusCodes.Status415UnsupportedMediaType, new { message = "This file type has no visual preview." });
        if (item.Size > MaxVisualBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { message = "This file is too large to preview." });
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(await backend.ReadAsync(fullPath, HttpContext.RequestAborted), contentType);
    }

    [HttpGet("text")]
    public async Task<IActionResult> Text([FromQuery] string path)
    {
        var (backend, fullPath, item) = await LocateAsync(path);
        if (item is null || item.IsFolder) return NotFound(new { message = "File not found." });
        var extension = Path.GetExtension(item.Name);
        var isDocx = extension.Equals(".docx", StringComparison.OrdinalIgnoreCase);
        if (!isDocx && !TextTypes.Contains(extension))
            return StatusCode(StatusCodes.Status415UnsupportedMediaType, new { message = "This file type has no text preview." });
        if (item.Size > (isDocx ? MaxDocumentBytes : MaxTextBytes))
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { message = "This file is too large to preview." });

        Response.Headers.CacheControl = "no-store";
        await using var source = await backend.ReadAsync(fullPath, HttpContext.RequestAborted);
        try
        {
            string value;
            if (isDocx) value = await DocxTextAsync(source, HttpContext.RequestAborted);
            else
            {
                using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                value = await reader.ReadToEndAsync(HttpContext.RequestAborted);
            }
            var truncated = value.Length > MaxPreviewCharacters;
            return Ok(new { text = truncated ? value[..MaxPreviewCharacters] : value, truncated });
        }
        catch (Exception error) when (error is InvalidDataException or XmlException)
        {
            return UnprocessableEntity(new { message = "This document could not be previewed." });
        }
    }

    private static async Task<string> DocxTextAsync(Stream source, CancellationToken cancellationToken)
    {
        await using var buffered = new MemoryStream();
        await source.CopyToAsync(buffered, cancellationToken);
        buffered.Position = 0;
        using var archive = new ZipArchive(buffered, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("Missing document body.");
        if (entry.Length > 4L * 1024 * 1024) throw new InvalidDataException("Document body is too large.");
        await using var xmlStream = entry.Open();
        await using var xmlBytes = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await xmlStream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (xmlBytes.Length + count > 4L * 1024 * 1024) throw new InvalidDataException("Document body is too large.");
            await xmlBytes.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        xmlBytes.Position = 0;
        using var xmlReader = XmlReader.Create(xmlBytes, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(xmlReader);
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var lines = document.Descendants(word + "p").Select(paragraph =>
            string.Concat(paragraph.Descendants().Where(node => node.Name == word + "t" || node.Name == word + "tab")
                .Select(node => node.Name == word + "tab" ? "\t" : node.Value)));
        return string.Join('\n', lines);
    }
}
