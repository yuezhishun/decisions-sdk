using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Codex.Decisions;

internal sealed partial class DecisionProtocolInput(bool jev, bool enableImages = false, bool enableLongContext = false)
{
    private const int ImageMaxBytes = 16 * 1024 * 1024;
    private const int TotalImageBytes = 32 * 1024 * 1024;
    internal static readonly JsonSerializerOptions ContentJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly List<object> history = [];
    private readonly List<object> images = [];
    private readonly Dictionary<string, (string Url, int Bytes)> requestedAssets = new(StringComparer.Ordinal);
    private string[] referenceKeys = [];
    private readonly HashSet<string> usedAssets = new(StringComparer.Ordinal);
    private CodexAppServer server = null!;
    private int pictureCount;
    private int imageBytes;
    private long expandedImageBytes;

    internal bool Jev => jev;

    internal async Task<DecisionJob> ParseAsync(CodexAppServer backend, JsonElement request, CancellationToken ct)
    {
        server = backend;
        Object(request, null, jev ? ["model", "state", "questions", "assets"] :
            ["model", "input", "questions", "prompt_cache_key", "prompt_cache_options", "safety_identifier"]);
        var model = String(Required(request, "model", "model"), "model");
        if (model != server.Model) throw Invalid("model 必须与当前 Codex 会话的模型匹配。", "model");
        if (enableLongContext)
            throw Unsupported("当前 Codex 后端不支持 Jev 长上下文开关。", "TypeSafe-Danger-Zone-Long-Context");
        return jev ? ParseJev(request) : await ParseOpenAiAsync(request, ct).ConfigureAwait(false);
    }

    private DecisionJob ParseJev(JsonElement request)
    {
        if (Value(request, "assets") is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } assets)
        {
            Object(assets, "assets");
            CheckVision("assets");
            var referenceBytes = 0;
            foreach (var asset in assets.EnumerateObject())
            {
                var path = "assets." + asset.Name;
                var keyBytes = Encoding.UTF8.GetByteCount(asset.Name);
                referenceBytes += keyBytes;
                if (string.IsNullOrWhiteSpace(asset.Name) || keyBytes > 1024 || referenceBytes > 65536)
                    throw Invalid("无效的图像引用名。", path);
                Object(asset.Value, path, ["type", "format", "data_base64", "part"]);
                if (!Is(asset.Value, "type", "image")) throw Invalid("仅支持图片资产。", path + ".type");
                if (NonNull(asset.Value, "part"))
                    throw Invalid("multipart 图片必须由 HTTP 宿主转为内联资产。", path + ".part");
                var format = String(Required(asset.Value, "format", path + ".format"), path + ".format");
                var mime = format switch
                {
                    "png" => "image/png",
                    "jpg" or "jpeg" => "image/jpeg",
                    _ => throw Invalid("仅支持 PNG 和 JPEG 资产。", path + ".format")
                };
                var data = String(Required(asset.Value, "data_base64", path + ".data_base64"),
                    path + ".data_base64", 4 * ((ImageMaxBytes + 2) / 3));
                requestedAssets.Add(asset.Name, (DataUrl(data, mime, path, out var decoded), decoded));
            }
            referenceKeys = requestedAssets.Keys.OrderByDescending(key => key.Length).ToArray();
        }

        var state = Required(request, "state", "state");
        CheckStructured(state, "state");
        UseAssets(state);
        var questions = Required(request, "questions", "questions");
        Object(questions, "questions");
        Count(questions.EnumerateObject().Count(), 1, 400, "questions");
        List<DecisionQuestion> parsed = [];
        foreach (var question in questions.EnumerateObject())
        {
            var path = "questions." + question.Name;
            Object(question.Value, path, ["type", "instructions", "criteria"]);
            var type = String(Required(question.Value, "type", path + ".type"), path + ".type");
            var instructions = Value(question.Value, "instructions");
            if (instructions.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                CheckStructured(instructions, path + ".instructions");
                UseAssets(instructions);
            }
            var criteria = Value(question.Value, "criteria");
            object[] values;
            Dictionary<string, JsonElement>? legend = null;
            object? promptCriteria = criteria.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : criteria;
            switch (type)
            {
                case "noul":
                    if (promptCriteria is not null)
                    {
                        Object(criteria, path + ".criteria", ["true", "false"]);
                        foreach (var entry in criteria.EnumerateObject())
                            if (entry.Value.ValueKind != JsonValueKind.Null)
                                CheckStructured(entry.Value, path + ".criteria." + entry.Name);
                    }
                    values = [true, false];
                    break;
                case "choice":
                    Object(criteria, path + ".criteria");
                    Count(criteria.EnumerateObject().Count(), 1, 255, path + ".criteria");
                    var options = criteria.EnumerateObject().ToArray();
                    foreach (var entry in options)
                        if (entry.Value.ValueKind != JsonValueKind.Null)
                            CheckStructured(entry.Value, path + ".criteria." + entry.Name);
                    values = options.Select(option => (object)option.Name).ToArray();
                    promptCriteria = options.Select(option => new { value = option.Name, description = option.Value }).ToArray();
                    break;
                case "score":
                    var levels = Array(criteria, path + ".criteria", 2, 10);
                    foreach (var level in levels) CheckStructured(level, path + ".criteria");
                    values = Enumerable.Range(0, levels.Length).Select(value => (object)value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    legend = values.Select((value, index) => (Key: (string)value, Level: levels[index].Clone()))
                        .ToDictionary(item => item.Key, item => item.Level, StringComparer.Ordinal);
                    break;
                case "box":
                    CheckVision(path);
                    if (promptCriteria is not null) throw Invalid("Box 不接受 criteria。", path + ".criteria");
                    if (instructions.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                        throw Invalid("Box 需要定位说明。", path + ".instructions");
                    values = [];
                    break;
                default:
                    throw Invalid("未知的 Jev 题型。", path + ".type");
            }
            if (promptCriteria is not null) UseAssets(criteria);
            parsed.Add(new(question.Name, null, type,
                instructions.ValueKind == JsonValueKind.Undefined ? null : instructions, promptCriteria, values, Legend: legend));
        }
        if (parsed.Any(item => item.Type == "box") && usedAssets.Count == 0)
            throw Invalid("Box 至少需要一张在请求中引用的图片。", "assets");
        if (usedAssets.Count != requestedAssets.Count)
            throw new DecisionProtocolException(400, "invalid_request", "每个图像资产必须在请求中引用。", "assets");
        if (expandedImageBytes + Encoding.UTF8.GetByteCount(state.GetRawText()) +
            Encoding.UTF8.GetByteCount(questions.GetRawText()) > TotalImageBytes)
            throw new DecisionProtocolException(413, "request_too_large", "展开后的 Jev 请求超过 32 MiB。", "assets");
        foreach (var asset in requestedAssets.Where(asset => usedAssets.Contains(asset.Key)))
        {
            images.Add(new { type = "text", text = "Image evidence referenced as: " + JsonSerializer.Serialize(asset.Key) });
            images.Add(new { type = "image", url = asset.Value.Url, detail = "auto" });
        }
        return new(state, history, images, parsed);
    }

    private void UseAssets(JsonElement value)
    {
        if (referenceKeys.Length == 0) return;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            for (var index = 0; index < text.Length;)
            {
                var match = referenceKeys.FirstOrDefault(key => text.AsSpan(index).StartsWith(key, StringComparison.Ordinal));
                if (match is null) index++;
                else
                {
                    usedAssets.Add(match);
                    expandedImageBytes += requestedAssets[match].Bytes - Encoding.UTF8.GetByteCount(match);
                    if (expandedImageBytes > TotalImageBytes)
                        throw new DecisionProtocolException(413, "request_too_large", "展开后的 Jev 请求超过 32 MiB。", "assets");
                    index += match.Length;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) UseAssets(property.Value);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray()) UseAssets(element);
    }

    private int CheckStructured(JsonElement value, string parameter, int depth = 0, int maxDepth = 64)
    {
        if (depth == 0 && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Array or JsonValueKind.Object))
            throw Invalid("需要字符串、对象或数组。", parameter);
        if (depth > maxDepth || value.ValueKind == JsonValueKind.Number &&
            (!value.TryGetDouble(out var number) || !double.IsFinite(number)))
            throw new DecisionProtocolException(400, "invalid_request", "JSON 嵌套深度或数值无效。", parameter);
        var count = 1;
        if (value.ValueKind == JsonValueKind.Object)
        {
            Object(value, parameter);
            foreach (var property in value.EnumerateObject()) count += CheckStructured(property.Value, parameter, depth + 1, maxDepth);
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray()) count += CheckStructured(element, parameter, depth + 1, maxDepth);
        return count;
    }

    private void CheckVision(string parameter)
    {
        if (jev && !enableImages) throw Invalid("需要启用 Jev 图片功能。", parameter);
        if (!server.SupportsImages) throw Unsupported("当前 Codex 模型不支持图像输入。", parameter);
    }

    private JsonElement Required(JsonElement obj, string property, string? parameter) =>
        obj.TryGetProperty(property, out var value) ? value : throw Invalid("缺少请求必填字段。", parameter);

    private string String(JsonElement value, string parameter, int maximum = 1_048_576)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length <= maximum) return text;
        throw Invalid("需要合法长度的字符串。", parameter);
    }

    private string? OptionalString(JsonElement obj, string property, string path, int maximum = 1_048_576) =>
        NonNull(obj, property) ? String(Value(obj, property), path + "." + property, maximum) : null;

    private JsonElement[] Array(JsonElement value, string parameter, int min, int max)
    {
        if (value.ValueKind != JsonValueKind.Array) throw Invalid("需要数组。", parameter);
        Count(value.GetArrayLength(), min, max, parameter);
        return value.EnumerateArray().ToArray();
    }

    private void Count(int count, int min, int max, string parameter)
    {
        if (count < min || count > max) throw Invalid($"数组或映射需要含有 {min} 至 {max} 项。", parameter);
    }

    private void Object(JsonElement value, string? parameter, string[]? allowed = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("需要 JSON 对象。", parameter);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name) || allowed is not null && !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw Invalid("字段重复或不受支持。", parameter is null ? property.Name : parameter + "." + property.Name);
    }

    internal DecisionProtocolException Invalid(string message, string? parameter) =>
        new(jev ? 422 : 400, "invalid_request", message, parameter);

    private static DecisionProtocolException Unsupported(string message, string parameter) =>
        new(400, "unsupported_capability", message, parameter);

    private static JsonElement Value(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) ? value : default;

    private static bool NonNull(JsonElement obj, string name) =>
        Value(obj, name).ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);

    private static bool Is(JsonElement obj, string field, string expected) =>
        Value(obj, field) is { ValueKind: JsonValueKind.String } value && value.GetString() == expected;
}
