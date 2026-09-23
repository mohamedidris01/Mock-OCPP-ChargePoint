using System.Text;
using System.Text.Json.Nodes;

namespace MockOcpp.Protocol;

/// <summary>What a CS-&gt;CP handler returns: a CALLRESULT payload, or a CALLERROR.</summary>
public readonly struct CallResult
{
    public bool Ok { get; }
    public JsonObject Payload { get; }
    public string ErrorCode { get; }
    public string ErrorDescription { get; }

    private CallResult(bool ok, JsonObject payload, string code, string desc)
    {
        Ok = ok;
        Payload = payload;
        ErrorCode = code;
        ErrorDescription = desc;
    }

    public static CallResult Success(JsonObject? payload = null)
        => new(true, payload ?? new JsonObject(), "", "");

    public static CallResult Fail(string code, string description)
        => new(false, new JsonObject(), code, description);
}

/// <summary>Thrown when the CSMS answers a CP-&gt;CS call with a CALLERROR.</summary>
public sealed class OcppCallException(string action, string code, string description)
    : Exception($"{action} → CALLERROR {code}: {description}")
{
    public string Action { get; } = action;
    public string Code { get; } = code;
    public string Description { get; } = description;
}
