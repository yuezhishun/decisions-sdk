using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using SkiaSharp;

internal sealed class DemoRequestException(int statusCode, string code, string message, string? parameter = null)
    : Exception(message)
{
    internal int StatusCode { get; } = statusCode;
    internal string Code { get; } = code;
    internal string? Parameter { get; } = parameter;
}

internal static class JevTransport
{
    private const int RequestLimit = 32 * 1024 * 1024;
    private const int ImageLimit = 16 * 1024 * 1024;
    private const int ValueDepthLimit = 64;
    private const int EnvelopeDepthLimit = ValueDepthLimit + 3;
    private const string RequestTooLarge = "The request body exceeds 32 MiB.";
    private const string ImageTooLarge = "An image exceeds 16 MiB.";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<JsonElement> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > RequestLimit) throw TooLarge(RequestTooLarge);
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType))
            throw new DemoRequestException(415, "unsupported_media_type", "Send application/json or multipart/form-data.");

        var multipart = contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase);
        if (!multipart && !IsJson(contentType))
            throw new DemoRequestException(415, "unsupported_media_type", "Send UTF-8 application/json or multipart/form-data.");

        var encoding = ReadEncoding(request);
        using var wireBody = await ReadLimitedAsync(request.Body, RequestLimit, RequestTooLarge, ct);
        using var decoded = encoding is null ? null : Decompress(wireBody, encoding, ct);
        var body = decoded ?? wireBody;
        var images = request.Headers.TryGetValue("TypeSafe-Danger-Zone-Images", out var flag) && flag.Count == 1 && flag[0] == "true";

        if (!multipart)
        {
            using var json = ParseJson(body);
            return await ReadAssetsAsync(json.RootElement, images, null, ct);
        }

        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 70)
            throw BadMultipart("Supply a valid multipart boundary.");

        try
        {
            var parts = new MultipartReader(boundary, body);
            var first = await parts.ReadNextSectionAsync(ct);
            if (first is null || PartName(first) != "request" ||
                !MediaTypeHeaderValue.TryParse(first.ContentType, out var jsonType) || !IsJson(jsonType))
                throw BadMultipart("The first multipart section must be named request and contain UTF-8 application/json.");

            using var jsonBody = await ReadLimitedAsync(first.Body, RequestLimit, RequestTooLarge, ct);
            using var json = ParseJson(jsonBody);
            return await ReadAssetsAsync(json.RootElement, images, parts, ct);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            throw BadMultipart("The multipart body is malformed or incomplete.");
        }
    }

    private static string? ReadEncoding(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderNames.ContentEncoding, out var values)) return null;
        var coding = values.Count == 1 ? values[0]?.Trim().ToLowerInvariant() : null;
        return coding is "gzip" or "x-gzip" or "zstd" ? coding
            : throw new DemoRequestException(415, "unsupported_content_encoding", "Use one gzip, x-gzip or zstd content encoding.");
    }

    private static MemoryStream Decompress(MemoryStream wire, string encoding, CancellationToken ct)
    {
        using var gzip = encoding == "zstd" ? null : new GZipDecoder();
        using var zstd = encoding == "zstd" ? new ZstandardDecoder(new ZstandardDecompressionOptions { MaxWindowLog2 = 23 }) : default;
        var source = Buffer(wire).Span;
        var buffer = new byte[64 * 1024];
        var decoded = new MemoryStream();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int consumed, written;
                var status = gzip is not null
                    ? gzip.Decompress(source, buffer, out consumed, out written)
                    : zstd!.Decompress(source, buffer, out consumed, out written);
                source = source[consumed..];
                if (decoded.Length + written > RequestLimit) throw TooLarge(RequestTooLarge);
                decoded.Write(buffer.AsSpan(0, written));

                if (status == OperationStatus.Done)
                {
                    if (source.IsEmpty)
                    {
                        decoded.Position = 0;
                        return decoded;
                    }
                    if (consumed == 0) break;
                    if (gzip is not null) gzip.Reset();
                    else zstd!.Reset();
                }
                else if (status != OperationStatus.DestinationTooSmall || consumed == 0 && written == 0) break;
            }
            throw BadCompression();
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            decoded.Dispose();
            throw BadCompression();
        }
        catch
        {
            decoded.Dispose();
            throw;
        }
    }

    private static async Task<MemoryStream> ReadLimitedAsync(Stream input, int limit, string limitMessage, CancellationToken ct)
    {
        var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var size = await input.ReadAsync(buffer, ct);
                if (size == 0) break;
                if (output.Length + size > limit) throw TooLarge(limitMessage);
                output.Write(buffer.AsSpan(0, size));
            }
            output.Position = 0;
            return output;
        }
        catch (BadHttpRequestException e) when (e.StatusCode == 413)
        {
            output.Dispose();
            throw TooLarge(limitMessage);
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static JsonDocument ParseJson(MemoryStream body)
    {
        var bytes = Buffer(body);
        if (bytes.Span.StartsWith(Encoding.UTF8.Preamble))
            throw new DemoRequestException(400, "invalid_encoding", "The JSON body must be UTF-8 without a byte order mark.");
        try { _ = StrictUtf8.GetCharCount(bytes.Span); }
        catch (DecoderFallbackException)
        {
            throw new DemoRequestException(400, "invalid_encoding", "The JSON body contains invalid UTF-8.");
        }
        var reader = new Utf8JsonReader(bytes.Span, new JsonReaderOptions { MaxDepth = EnvelopeDepthLimit });
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.Number && (!reader.TryGetDouble(out var number) || !double.IsFinite(number)))
                throw new DemoRequestException(400, "invalid_number", "JSON numbers must be finite.");
            if (!reader.ValueIsEscaped || reader.ValueSpan.IndexOf("\\u"u8) < 0) continue;
            try { _ = reader.GetString(); }
            catch (InvalidOperationException)
            {
                throw new DemoRequestException(400, "invalid_encoding", "The JSON body contains invalid Unicode escapes.");
            }
        }
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = EnvelopeDepthLimit });
    }

    private static void ValidateJsonValues(JsonElement value, int depth = 0)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                if (depth >= ValueDepthLimit) throw NestingError();
                foreach (var pair in value.EnumerateObject()) ValidateJsonValues(pair.Value, depth + 1);
                break;
            case JsonValueKind.Array:
                if (depth >= ValueDepthLimit) throw NestingError();
                foreach (var item in value.EnumerateArray()) ValidateJsonValues(item, depth + 1);
                break;
        }
    }

    private static async Task<JsonElement> ReadAssetsAsync(JsonElement root, bool images, MultipartReader? parts, CancellationToken ct)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw Validation("The body must be a JSON object.");
        var state = Required(root, "state", "State is required.");
        if (state.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array))
            throw Validation("State must be a string, object or array.", "state");
        ValidateJsonValues(state);
        if (Required(root, "model", "Model is required.").ValueKind != JsonValueKind.String)
            throw Validation("Model must be a string.", "model");
        var questions = Required(root, "questions", "Questions are required.");
        if (questions.ValueKind != JsonValueKind.Object || !questions.EnumerateObject().Any())
            throw Validation("Questions must be a non-empty object.", "questions");
        var box = false;
        foreach (var question in questions.EnumerateObject())
        {
            if (question.Value.ValueKind != JsonValueKind.Object) continue;
            if (question.Value.TryGetProperty("instructions", out var instructions)) ValidateJsonValues(instructions);
            if (question.Value.TryGetProperty("criteria", out var criteria)) ValidateJsonValues(criteria);
            if (question.Value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.ValueEquals("box")) box = true;
        }

        var hasAssets = root.TryGetProperty("assets", out var assets);
        if (hasAssets && (assets.ValueKind != JsonValueKind.Object || !assets.EnumerateObject().Any()))
            throw Validation("Assets must be a non-empty object.", "assets");
        if ((hasAssets || box) && !images)
            throw new DemoRequestException(400, "images_disabled", "Images and Box questions require TypeSafe-Danger-Zone-Images: true.");

        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
        var normalized = new Dictionary<string, (string Format, byte[] Bytes)>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var referenceBytes = 0;
        var processedBytes = 0;
        if (hasAssets)
        {
            foreach (var asset in assets.EnumerateObject())
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(asset.Name)) throw BadImage("An image reference is empty.");
                if (!names.Add(asset.Name)) throw BadImage("Image references must be unique.");
                var length = StrictUtf8.GetByteCount(asset.Name);
                referenceBytes += length;
                if (length > 1024 || referenceBytes > 65536) throw TooLarge("The image references exceed their length limit.");
                if (asset.Value.ValueKind != JsonValueKind.Object) throw Validation("An asset must be an object.", "assets." + asset.Name);
                var format = OptionalAssetString(asset.Value, "format");
                var type = OptionalAssetString(asset.Value, "type");
                if (type is not (null or "image") || format is not (null or "png" or "jpg" or "jpeg"))
                    throw BadImage("An asset must be a PNG or JPEG image.");
                var part = OptionalAssetString(asset.Value, "part");
                if (part is not null)
                {
                    if (parts is null || string.IsNullOrWhiteSpace(part) || part == "request" || !pending.TryAdd(part, asset.Name))
                        throw BadMultipart("Every image part must be named by exactly one asset, and request is reserved.");
                }
                else
                {
                    var base64 = OptionalAssetString(asset.Value, "data_base64");
                    if (format is null || string.IsNullOrEmpty(base64) || base64.Any(char.IsWhiteSpace))
                        throw BadImage("An inline image requires a format and padded Base64 data.");
                    if (base64.Length > 4 * ((ImageLimit + 2) / 3)) throw TooLarge(ImageTooLarge);
                    byte[] decoded;
                    try { decoded = Convert.FromBase64String(base64); }
                    catch (FormatException) { throw BadImage("Invalid Base64 image data."); }
                    if (Convert.ToBase64String(decoded) != base64) throw BadImage("Invalid Base64 image data.");
                    AddImage(asset.Name, decoded);
                }
            }
        }

        if (parts is not null)
        {
            while (await parts.ReadNextSectionAsync(ct) is { } section)
            {
                var name = PartName(section);
                if (!pending.Remove(name, out var reference))
                    throw BadMultipart("A multipart image is unclaimed or duplicated.");
                if (section.ContentType is not null &&
                    (!MediaTypeHeaderValue.TryParse(section.ContentType, out var imageType) ||
                    imageType.MediaType.Value?.ToLowerInvariant() is not ("image/png" or "image/jpeg" or "image/jpg" or "application/octet-stream")))
                    throw BadMultipart("Unsupported multipart image content type.");

                using var file = await ReadLimitedAsync(section.Body, ImageLimit, ImageTooLarge, ct);
                AddImage(reference, Buffer(file).Span);
            }
        }
        if (pending.Count != 0) throw BadMultipart("A referenced multipart image part is missing.");
        if (!hasAssets) return root.Clone();

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { MaxDepth = EnvelopeDepthLimit }))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
                if (!property.NameEquals("assets")) property.WriteTo(writer);
            writer.WriteStartObject("assets");
            foreach (var image in normalized)
            {
                writer.WriteStartObject(image.Key);
                writer.WriteString("type", "image");
                writer.WriteString("format", image.Value.Format);
                writer.WriteBase64String("data_base64", image.Value.Bytes);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        using var json = JsonDocument.Parse(Buffer(output), new JsonDocumentOptions { MaxDepth = EnvelopeDepthLimit });
        return json.RootElement.Clone();

        void AddImage(string reference, ReadOnlySpan<byte> bytes)
        {
            ct.ThrowIfCancellationRequested();
            var image = NormalizeImage(bytes);
            processedBytes += image.Bytes.Length;
            if (processedBytes > RequestLimit) throw TooLarge("The processed images exceed 32 MiB.");
            normalized.Add(reference, image);
        }
    }

    private static (string Format, byte[] Bytes) NormalizeImage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > ImageLimit) throw TooLarge(ImageTooLarge);
        if (bytes.IsEmpty) throw BadImage("The image is empty.");
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg))
            throw BadImage("The image must be a valid PNG or JPEG file.");

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width > 1568 || height > 1568) throw TooLarge("Image dimensions must not exceed 1568 pixels per side.");
        if (width < 280 || height < 280) throw BadImage("Image dimensions must be at least 280 pixels per side.");
        var png = codec.EncodedFormat == SKEncodedImageFormat.Png;
        if (codec.FrameCount > 1) throw BadImage("Animated images are unsupported.");
        if (png) ValidatePngChunks(bytes);
        else if (bytes is not [.., 0xff, 0xd9]) throw BadImage("The JPEG end marker is missing.");

        var format = codec.EncodedFormat;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, codec.Info.AlphaType);
        using var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw BadImage("The image is damaged or incomplete.");

        // Re-encode decoded pixels without carrying the upload's metadata into the model request.
        using var encoded = bitmap.Encode(format, 100) ?? throw BadImage("The image could not be encoded.");
        if (encoded.Size > ImageLimit) throw TooLarge(ImageTooLarge);
        return (png ? "png" : "jpeg", encoded.ToArray());
    }

    private static void ValidatePngChunks(ReadOnlySpan<byte> png)
    {
        png = png[8..];
        while (png.Length >= 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png);
            if (length > png.Length - 12) break;
            var type = png[4..8];
            if (type.SequenceEqual("acTL"u8)) throw BadImage("Animated images are unsupported.");
            if (type.SequenceEqual("IEND"u8))
            {
                if (length == 0 && BinaryPrimitives.ReadUInt32BigEndian(png[8..]) == 0xae426082)
                    return;
                break;
            }
            png = png[(12 + (int)length)..];
        }
        throw BadImage("The PNG end chunk is missing or incomplete.");
    }

    private static bool IsJson(MediaTypeHeaderValue media) =>
        media.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
        (!media.Charset.HasValue || HeaderUtilities.RemoveQuotes(media.Charset).Equals("utf-8", StringComparison.OrdinalIgnoreCase));

    private static string PartName(MultipartSection section)
    {
        if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) &&
            disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase) &&
            HeaderUtilities.RemoveQuotes(disposition.Name).Value is { } name && !string.IsNullOrWhiteSpace(name))
            return name;
        throw BadMultipart("Every multipart section must have a non-empty form-data name.");
    }

    private static ReadOnlyMemory<byte> Buffer(MemoryStream stream) => stream.GetBuffer().AsMemory(0, (int)stream.Length);

    private static JsonElement Required(JsonElement element, string name, string message) =>
        element.TryGetProperty(name, out var value) ? value : throw new DemoRequestException(422, "missing", message, name);

    private static string? OptionalAssetString(JsonElement asset, string name) =>
        !asset.TryGetProperty(name, out var value) ? null : value.ValueKind == JsonValueKind.String ? value.GetString()
            : throw BadImage($"Asset {name} must be a string.");

    private static DemoRequestException BadCompression() => new(400, "invalid_content_encoding", "The compressed request is malformed, incomplete or exceeds the allowed zstd window.");
    private static DemoRequestException NestingError() => new(400, "invalid_nesting", "State, instructions and criteria may be nested at most 64 container levels.");
    private static DemoRequestException TooLarge(string message) => new(413, "request_too_large", message);
    private static DemoRequestException BadMultipart(string message) => new(400, "invalid_multipart", message);
    private static DemoRequestException BadImage(string message) => new(400, "invalid_asset", message);
    private static DemoRequestException Validation(string message, string? parameter = null) => new(422, "validation_error", message, parameter);
}
