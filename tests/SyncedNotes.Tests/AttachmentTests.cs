using System.Net;
using Microsoft.EntityFrameworkCore;
using SyncedNotes.Endpoints;
using SyncedNotes.Tests.Infrastructure;
using static SyncedNotes.Tests.Infrastructure.Api;

namespace SyncedNotes.Tests;

/// <summary>PUT /api/attachments/{id} and GET /attachments/{id}.</summary>
public class AttachmentTests(SyncedNotesFactory factory) : IClassFixture<SyncedNotesFactory>
{
    private async Task<HttpClient> NewUserClientAsync() => factory.CreateClientFor(await factory.CreateUserAsync());

    [Fact]
    public async Task An_uploaded_png_is_served_back_with_private_caching_and_a_sandboxing_policy()
    {
        var client = await NewUserClientAsync();
        var id = Guid.NewGuid();
        var png = Png();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/attachments/{id}") { Content = Bytes(png, "image/png") };
        request.Headers.Add("X-File-Name", Uri.EscapeDataString("My photo.png"));

        var upload = await (await client.SendAsync(request)).ReadJsonAsync();
        Assert.Equal(id, upload.GetProperty("id").GetGuid());
        Assert.Equal(png.Length, upload.GetProperty("size").GetInt64());
        Assert.Equal("image/png", upload.Str("contentType"));

        var download = await client.GetAsync($"/attachments/{id}");
        await download.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(png, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", download.Content.Headers.ContentType!.MediaType);

        var cache = download.Headers.CacheControl!;
        Assert.True(cache.Private);
        Assert.Equal(TimeSpan.FromSeconds(31_536_000), cache.MaxAge);
        Assert.Contains(cache.Extensions, e => e.Name == "immutable");

        var csp = Assert.Single(download.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("sandbox", csp);
        Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal($"\"{id:N}\"", download.Headers.ETag!.Tag);

        var disposition = download.Content.Headers.ContentDisposition!;
        Assert.Equal("inline", disposition.DispositionType);
        Assert.Equal("My%20photo.png", disposition.FileName!.Trim('"'));
    }

    public static TheoryData<string, byte[], string> SupportedImages => new()
    {
        { "png", Png(), "image/png" },
        { "jpeg", Jpeg(), "image/jpeg" },
        { "gif", [.. "GIF89a"u8, 1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0], "image/gif" },
        { "webp", [.. "RIFF"u8, 0x24, 0, 0, 0, .. "WEBP"u8, .. "VP8 "u8], "image/webp" },
        { "bmp", [.. "BM"u8, .. new byte[24]], "image/bmp" },
        { "avif", [0, 0, 0, 0x1C, .. "ftyp"u8, .. "avif"u8, 0, 0, 0, 0], "image/avif" },
    };

    [Theory]
    [MemberData(nameof(SupportedImages))]
    public async Task Image_types_are_detected_from_the_content_not_the_declared_type(string format, byte[] data, string expectedType)
    {
        var client = await NewUserClientAsync();
        var id = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/attachments/{id}") { Content = Bytes(data, "application/octet-stream") };
        request.Headers.Add("X-File-Name", $"picture.{format}");

        var upload = await (await client.SendAsync(request)).ReadJsonAsync();

        Assert.Equal(expectedType, upload.Str("contentType"));
        var download = await client.GetAsync($"/attachments/{id}");
        await download.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(expectedType, download.Content.Headers.ContentType!.MediaType);
        Assert.Equal(data, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal($"picture.{format}", download.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
    }

    public static TheoryData<string, byte[]> NotImages => new()
    {
        { "text", "Hello, this is definitely not an image."u8.ToArray() },
        { "svg", """<svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)"><script>alert(1)</script></svg>"""u8.ToArray() },
        { "xml svg", """<?xml version="1.0"?><svg xmlns="http://www.w3.org/2000/svg"></svg>"""u8.ToArray() },
        { "html", "<!DOCTYPE html><html><script>alert(document.cookie)</script></html>"u8.ToArray() },
        { "garbage", [0x00, 0x13, 0x37, 0x42, 0x99, 0xAB, 0xCD, 0xEF, 0x10, 0x20, 0x30, 0x40, 0x50] },
        { "truncated png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A] },
        { "empty", [] },
    };

    [Theory]
    [MemberData(nameof(NotImages))]
    public async Task Anything_that_is_not_a_supported_image_is_rejected(string kind, byte[] data)
    {
        var client = await NewUserClientAsync();
        var id = Guid.NewGuid();

        // Even when the client claims it is a PNG.
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/attachments/{id}") { Content = Bytes(data, "image/png") };
        request.Headers.Add("X-File-Name", Uri.EscapeDataString($"{kind}.png"));
        var response = await client.SendAsync(request);

        await response.ReadProblemAsync(HttpStatusCode.UnsupportedMediaType);
        await client.GetAsync($"/attachments/{id}").AssertStatusAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Images_larger_than_15_MB_are_rejected()
    {
        var client = await NewUserClientAsync();
        var tooBig = Png(padding: (int)NoteEndpoints.MaxAttachmentBytes - Png().Length + 1);
        Assert.Equal(NoteEndpoints.MaxAttachmentBytes + 1, tooBig.Length);

        var declared = await client.UploadAsync(Guid.NewGuid(), tooBig);
        await declared.ReadProblemAsync(HttpStatusCode.RequestEntityTooLarge);

        // Without a Content-Length (a streamed upload) the limit is enforced while reading the body.
        var streamedId = Guid.NewGuid();
        var streamed = await client.PutAsync($"/api/attachments/{streamedId}", new UnknownLengthContent(tooBig));
        await streamed.ReadProblemAsync(HttpStatusCode.RequestEntityTooLarge);
        await client.GetAsync($"/attachments/{streamedId}").AssertStatusAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Images_of_exactly_15_MB_are_accepted()
    {
        var client = await NewUserClientAsync();
        var id = Guid.NewGuid();
        var exact = Png(padding: (int)NoteEndpoints.MaxAttachmentBytes - Png().Length);

        var upload = await (await client.UploadAsync(id, exact)).ReadJsonAsync();

        Assert.Equal(NoteEndpoints.MaxAttachmentBytes, upload.GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task Uploads_are_idempotent_and_never_replace_the_stored_image()
    {
        var client = await NewUserClientAsync();
        var id = Guid.NewGuid();
        var original = Png();
        await client.UploadAsync(id, original).AssertStatusAsync(HttpStatusCode.OK);

        // The client retries until it sees success; a retry (even with other bytes) just reports success.
        var retry = await (await client.UploadAsync(id, Jpeg())).ReadJsonAsync();
        Assert.Equal(id, retry.GetProperty("id").GetGuid());
        Assert.Equal(original.Length, retry.GetProperty("size").GetInt64());

        var download = await client.GetAsync($"/attachments/{id}");
        Assert.Equal(original, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal(1, await factory.WithDbAsync(db => db.Attachments.CountAsync(a => a.Id == id)));
    }

    [Fact]
    public async Task Downloads_support_conditional_requests()
    {
        var client = await NewUserClientAsync();
        var id = Guid.NewGuid();
        await client.UploadAsync(id, Png()).AssertStatusAsync(HttpStatusCode.OK);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/attachments/{id}");
        request.Headers.IfNoneMatch.ParseAdd($"\"{id:N}\"");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Unknown_images_are_404_and_anonymous_requests_are_401_not_redirects()
    {
        var client = await NewUserClientAsync();
        await (await client.GetAsync($"/attachments/{Guid.NewGuid()}")).ReadProblemAsync(HttpStatusCode.NotFound);
        await client.GetAsync("/attachments/not-a-guid").AssertStatusAsync(HttpStatusCode.NotFound);

        var id = Guid.NewGuid();
        await client.UploadAsync(id, Png()).AssertStatusAsync(HttpStatusCode.OK);
        var anonymous = factory.CreateAnonymousClient();
        await anonymous.GetAsync($"/attachments/{id}").AssertStatusAsync(HttpStatusCode.Unauthorized);
        await anonymous.UploadAsync(Guid.NewGuid(), Png()).AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Client_supplied_file_names_are_sanitised()
    {
        var client = await NewUserClientAsync();
        var named = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/attachments/{named}") { Content = Bytes(Png()) };
        request.Headers.Add("X-File-Name", Uri.EscapeDataString("../../etc/passwd\r\n.png"));
        await (await client.SendAsync(request)).AssertStatusAsync(HttpStatusCode.OK);
        var unnamed = Guid.NewGuid();
        await client.UploadAsync(unnamed, Jpeg()).AssertStatusAsync(HttpStatusCode.OK);

        var names = await factory.WithDbAsync(db => db.Attachments.AsNoTracking()
            .Where(a => a.Id == named || a.Id == unnamed)
            .ToDictionaryAsync(a => a.Id, a => a.FileName));

        Assert.DoesNotContain('/', names[named]);
        Assert.DoesNotContain(names[named], char.IsControl);
        Assert.EndsWith(".png", names[named]);
        Assert.Equal("image.jpg", names[unnamed]);
        var download = await client.GetAsync($"/attachments/{named}");
        Assert.DoesNotContain("\n", download.Content.Headers.ContentDisposition!.ToString());
    }

    /// <summary>Request content without a known length, so the server can't rely on Content-Length.</summary>
    private sealed class UnknownLengthContent(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
