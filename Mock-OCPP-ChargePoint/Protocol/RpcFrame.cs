using System.Text.Json;
using System.Text.Json.Nodes;

namespace MockOcpp.Protocol;

/// <summary>
/// One parsed OCPP-J frame. OCPP-J is JSON-RPC carried as a bare JSON array:
///   CALL        [2, "uid", "Action", {payload}]
///   CALLRESULT  [3, "uid", {payload}]
///   CALLERROR   [4, "uid", "ErrorCode", "description", {details}]
/// </summary>
public sealed class RpcFrame
{
    public MessageType Type { get; init; }
    public string UniqueId { get; init; } = "";
    public string Action { get; init; } = "";
    public JsonObject Payload { get; init; } = new();
    public string ErrorCode { get; init; } = "";
    public string ErrorDescription { get; init; } = "";

    public static bool TryParse(string text, out RpcFrame frame, out string parseError)
    {
        frame = new RpcFrame();
        parseError = "";
        JsonArray? arr;
        try
        {
            arr = JsonNode.Parse(text) as JsonArray;
        }
        catch (JsonException ex)
        {
            parseError = "not valid JSON: " + ex.Message;
            return false;
        }

        if (arr is null || arr.Count < 3)
        {
            parseError = "message is not a JSON array of at least 3 elements";
            return false;
        }

        int typeId = arr[0]?.GetValue<int>() ?? -1;
        string uid = arr[1]?.GetValue<string>() ?? "";

        switch ((MessageType)typeId)
        {
            case MessageType.Call:
                if (arr.Count < 4)
                {
                    parseError = "CALL requires 4 elements";
                    return false;
                }
                frame = new RpcFrame
                {
                    Type = MessageType.Call,
                    UniqueId = uid,
                    Action = arr[2]?.GetValue<string>() ?? "",
                    Payload = AsObject(arr[3]),
                };
                return true;

            case MessageType.CallResult:
                frame = new RpcFrame
                {
                    Type = MessageType.CallResult,
                    UniqueId = uid,
                    Payload = AsObject(arr[2]),
                };
                return true;

            case MessageType.CallError:
                if (arr.Count < 5)
                {
                    parseError = "CALLERROR requires 5 elements";
                    return false;
                }
                frame = new RpcFrame
                {
                    Type = MessageType.CallError,
                    UniqueId = uid,
                    ErrorCode = arr[2]?.GetValue<string>() ?? "",
                    ErrorDescription = arr[3]?.GetValue<string>() ?? "",
                    Payload = AsObject(arr[4]),
                };
                return true;

            default:
                parseError = $"unknown MessageTypeId {typeId}";
                return false;
        }
    }

    private static JsonObject AsObject(JsonNode? node)
        => node is JsonObject o ? (JsonObject)o.DeepClone() : new JsonObject();

    // --- serializers ------------------------------------------------------

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private static JsonNode Body(JsonObject? payload)
        => payload is null ? new JsonObject() : (JsonNode)payload.DeepClone();

    public static string Call(string uid, string action, JsonObject? payload)
        => new JsonArray((int)MessageType.Call, uid, action, Body(payload)).ToJsonString(Compact);

    public static string Result(string uid, JsonObject? payload)
        => new JsonArray((int)MessageType.CallResult, uid, Body(payload)).ToJsonString(Compact);

    public static string Error(string uid, string code, string description, JsonObject? details = null)
        => new JsonArray((int)MessageType.CallError, uid, code, description, Body(details)).ToJsonString(Compact);
}
