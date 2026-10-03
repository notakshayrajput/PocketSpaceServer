using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using PocketSpaceServer.Authentication;

namespace PocketSpaceServer.Tests;

public class FilePreviewTests
{
    [Fact]
    public async Task PreviewRequiresAuthenticationAndKeepsAccountsPrivate()
    {
        await using var app = new TestApplication();
        using var owner = app.CreateClient();
        using var other = app.CreateClient();
        using var anonymous = await owner.GetAsync("/api/space/preview/content?path=picture.png");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        await SignUp(owner, "preview-owner");
        await SignUp(other, "preview-other");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lV8AAAAASUVORK5CYII=");
        using (var upload = await owner.PostAsync("/api/upload/stream?name=picture.png", new ByteArrayContent(png)))
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        using var image = await owner.GetAsync("/api/space/preview/content?path=picture.png");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await image.Content.ReadAsByteArrayAsync());
        Assert.Null(image.Content.Headers.ContentDisposition);
        using var foreign = await other.GetAsync("/api/space/preview/content?path=picture.png");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task TextAndDocxPreviewReturnInertReadableText()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        await SignUp(client, "preview-text");
        var html = "<script>alert('test')</script><p>Hello</p>";
        using (var upload = await client.PostAsync("/api/upload/stream?name=page.html",
            new ByteArrayContent(Encoding.UTF8.GetBytes(html))))
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        using var noVisual = await client.GetAsync("/api/space/preview/content?path=page.html");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, noVisual.StatusCode);
        using var text = await client.GetAsync("/api/space/preview/text?path=page.html");
        Assert.Equal(HttpStatusCode.OK, text.StatusCode);
        var plain = (await text.Content.ReadFromJsonAsync<TextResult>())!;
        Assert.Equal(html, plain.Text);
        Assert.False(plain.Truncated);

        await using var package = new MemoryStream();
        using (var zip = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("word/document.xml");
            await using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            await writer.WriteAsync("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>First line</w:t></w:r></w:p><w:p><w:r><w:t>Second line</w:t></w:r></w:p></w:body></w:document>");
        }
        using (var upload = await client.PostAsync("/api/upload/stream?name=notes.DOCX",
            new ByteArrayContent(package.ToArray())))
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        using var document = await client.GetAsync("/api/space/preview/text?path=notes.DOCX");
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        Assert.Equal("First line\nSecond line", (await document.Content.ReadFromJsonAsync<TextResult>())!.Text);
    }

    private static async Task SignUp(HttpClient client, string username)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/signup",
            new { username, password = "account@123" });
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);
    }

    private sealed record TextResult(string Text, bool Truncated);
}
