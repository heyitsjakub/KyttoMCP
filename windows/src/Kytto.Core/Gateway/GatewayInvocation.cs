namespace Kytto.Core.Gateway;

public sealed class GatewayInvocationException(string message) : Exception(message)
{
    public static GatewayInvocationException MissingValue(string option) =>
        new($"Gateway option {option} needs a value.");

    public static GatewayInvocationException MissingSeparator() =>
        new("Put -- before the real MCP server command.");

    public static GatewayInvocationException MissingCommand() =>
        new("Give Kytto the real MCP server command after --.");

    public static GatewayInvocationException ConflictingSources() =>
        new("Use either --route or a command after --, not both.");

    public static GatewayInvocationException UnknownOption(string option) =>
        new($"Unknown gateway option {option}.");
}

/// <summary>Everything the stdio helper needs to identify one upstream server.</summary>
public sealed record GatewayInvocation(
    string? RouteID,
    string? RoutesPath,
    string ServerID,
    string ClientID,
    string? EventLogPath,
    string? Command,
    IReadOnlyList<string> Arguments)
{
    public static GatewayInvocation Parse(IReadOnlyList<string> arguments)
    {
        var serverId = "unknown-server";
        var clientId = "unknown-client";
        string? eventLogPath = null;
        string? routeId = null;
        string? routesPath = null;

        for (var index = 0; index < arguments.Count;)
        {
            var option = arguments[index];
            if (option == "--")
            {
                if (routeId is not null) throw GatewayInvocationException.ConflictingSources();
                if (index + 1 >= arguments.Count) throw GatewayInvocationException.MissingCommand();
                return new GatewayInvocation(
                    null,
                    routesPath,
                    serverId,
                    clientId,
                    eventLogPath,
                    arguments[index + 1],
                    arguments.Skip(index + 2).ToArray());
            }

            if (option is not ("--route" or "--routes" or "--server-id" or "--client-id" or "--event-log"))
            {
                if (option.StartsWith('-')) throw GatewayInvocationException.UnknownOption(option);
                throw GatewayInvocationException.MissingSeparator();
            }
            if (index + 1 >= arguments.Count) throw GatewayInvocationException.MissingValue(option);

            var value = arguments[index + 1];
            switch (option)
            {
                case "--route": routeId = value.ToLowerInvariant(); break;
                case "--routes": routesPath = Path.GetFullPath(value); break;
                case "--server-id": serverId = value; break;
                case "--client-id": clientId = value; break;
                case "--event-log": eventLogPath = Path.GetFullPath(value); break;
            }
            index += 2;
        }

        return routeId is not null
            ? new GatewayInvocation(routeId, routesPath, serverId, clientId, eventLogPath, null, [])
            : throw GatewayInvocationException.MissingSeparator();
    }
}
