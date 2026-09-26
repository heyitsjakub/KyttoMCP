using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kytto.Core;
using Kytto.Core.Gateway;
using Kytto.Core.Imports;
using Kytto.Core.Model;
using Kytto.Core.Profiles;
using Kytto.Core.Secrets;
using Kytto.Core.Updates;

namespace Kytto.App.Ipc;

/// <summary>
/// How every reply is spelled: <c>{ ok: true, data }</c> or
/// <c>{ ok: false, error: { code, message } }</c>.
/// </summary>
/// <remarks>
/// Failures are data, not exceptions. The web layer renders an error state; it
/// never sees a rejected promise it has to guess the meaning of.
/// </remarks>
internal static class Envelope
{
    /// <summary>
    /// The one serializer configuration in the app.
    /// </summary>
    /// <remarks>
    /// Camel case rather than verbatim because it reproduces what Swift's
    /// <c>JSONEncoder</c> emitted from the macOS DTOs, which is what the web layer
    /// already reads: <c>ServerID</c> becomes <c>serverID</c>, <c>PathDisplay</c>
    /// becomes <c>pathDisplay</c>. The DTOs are named so that this holds, and
    /// <c>WireFormatTests</c> is what keeps it true.
    /// </remarks>
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Positional payload records are the IPC schema. Missing or explicit null
        // values for their non-nullable constructor parameters are malformed input,
        // not objects handlers should discover one NullReferenceException later.
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static string Success<T>(T data)
    {
        var writer = new ArrayBufferWriter<byte>();
        // Written by hand rather than through a wrapper type so `data` keeps its
        // own declared type: a generic envelope class would erase it to `object`
        // and serialize the runtime type's members instead.
        using (var json = new Utf8JsonWriter(writer))
        {
            json.WriteStartObject();
            json.WriteBoolean("ok", true);
            json.WritePropertyName("data");
            JsonSerializer.Serialize(json, data, Json);
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    internal static string Failure(Exception error) =>
        Failure(CodeFor(error), MessageFor(error));

    internal static string Failure(string code, string message)
    {
        var writer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(writer))
        {
            json.WriteStartObject();
            json.WriteBoolean("ok", false);
            json.WriteStartObject("error");
            json.WriteString("code", code);
            json.WriteString("message", message);
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    /// <summary>The stable code for one failure.</summary>
    /// <remarks>
    /// Also what anonymous diagnostics reports, which is why it is a closed set of
    /// spellings rather than a type name: the message may carry a config path or
    /// process output and never leaves the machine, so the classification has to be
    /// useful on its own.
    /// </remarks>
    internal static string CodeFor(Exception error) => error switch
    {
        BadArgumentException => "badArgument",
        IpcException ipc => ipc.Code,
        JsonException => "badPayload",
        GatewayManagedException or NoDriftException => "appState",
        ConfigWriteException or ConfigTransactionException => "configWrite",
        BackupException => "backup",
        ToggleException => "toggle",
        AuthoringException => "authoring",
        SecretsException => "secrets",
        ProfileException => "profile",
        GatewayMigrationException or GatewayRouteException => "gatewayMigration",
        UpdateCheckException => "updateCheck",
        UpdatePackageException => "updatePackage",
        ProvenanceCheckException => "provenanceCheck",
        AgentImportException => "agentImport",
        _ => "internal",
    };

    private static string MessageFor(Exception error) => error switch
    {
        JsonException => "The command payload did not match what this command expects.",
        _ => error.Message,
    };
}

/// <summary>An error with a code the web layer may branch on.</summary>
internal class IpcException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    internal static IpcException MalformedMessage() =>
        new("malformedMessage", "The message did not carry a command name.");

    internal static IpcException UnknownCommand(string name) =>
        new("unknownCommand", $"No such command: {name}. Commands are listed in docs/ipc.md.");
}

/// <summary>A caller-supplied argument that does not name anything real.</summary>
/// <remarks>
/// Reported as <c>badArgument</c>. Nothing in the UI branches on it — the message is
/// the useful half and is shown verbatim — but a diagnostics report that cannot tell
/// a bad payload from a genuine internal fault is not worth collecting.
/// </remarks>
internal sealed class BadArgumentException(string message) : IpcException("badArgument", message);
