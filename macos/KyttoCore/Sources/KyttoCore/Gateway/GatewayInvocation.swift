import Foundation

public enum GatewayInvocationError: Error, LocalizedError, Equatable {
    case missingValue(String)
    case missingSeparator
    case missingCommand
    case conflictingSources
    case unknownOption(String)

    public var errorDescription: String? {
        switch self {
        case .missingValue(let option):
            "Gateway option \(option) needs a value."
        case .missingSeparator:
            "Put -- before the real MCP server command."
        case .missingCommand:
            "Give Kytto the real MCP server command after --."
        case .conflictingSources:
            "Use either --route or a command after --, not both."
        case .unknownOption(let option):
            "Unknown gateway option \(option)."
        }
    }
}

/// Everything the small stdio shim needs to identify and launch one upstream
/// server. The route contains no secret values; those will be resolved by the
/// native control plane once gateway mode leaves preview.
public struct GatewayInvocation: Equatable, Sendable {
    public let routeID: String?
    public let routesURL: URL?
    public let serverID: String
    public let clientID: String
    public let eventLogURL: URL?
    public let command: String?
    public let arguments: [String]

    public init(
        routeID: String? = nil,
        routesURL: URL? = nil,
        serverID: String,
        clientID: String,
        eventLogURL: URL?,
        command: String?,
        arguments: [String]
    ) {
        self.routeID = routeID
        self.routesURL = routesURL
        self.serverID = serverID
        self.clientID = clientID
        self.eventLogURL = eventLogURL
        self.command = command
        self.arguments = arguments
    }

    public static func parse(_ arguments: [String]) throws -> GatewayInvocation {
        var serverID = "unknown-server"
        var clientID = "unknown-client"
        var eventLogURL: URL?
        var routeID: String?
        var routesURL: URL?
        var index = 0

        while index < arguments.count {
            let option = arguments[index]
            if option == "--" {
                guard routeID == nil else { throw GatewayInvocationError.conflictingSources }
                let commandIndex = index + 1
                guard commandIndex < arguments.count else {
                    throw GatewayInvocationError.missingCommand
                }
                return GatewayInvocation(
                    routeID: nil,
                    routesURL: routesURL,
                    serverID: serverID,
                    clientID: clientID,
                    eventLogURL: eventLogURL,
                    command: arguments[commandIndex],
                    arguments: Array(arguments.dropFirst(commandIndex + 1))
                )
            }

            guard ["--route", "--routes", "--server-id", "--client-id", "--event-log"].contains(option) else {
                if option.hasPrefix("-") {
                    throw GatewayInvocationError.unknownOption(option)
                }
                throw GatewayInvocationError.missingSeparator
            }
            guard index + 1 < arguments.count else {
                throw GatewayInvocationError.missingValue(option)
            }
            let value = arguments[index + 1]
            switch option {
            case "--route":
                routeID = value.lowercased()
            case "--routes":
                routesURL = URL(filePath: value)
            case "--server-id":
                serverID = value
            case "--client-id":
                clientID = value
            case "--event-log":
                eventLogURL = URL(filePath: value)
            default:
                break
            }
            index += 2
        }

        if let routeID {
            return GatewayInvocation(
                routeID: routeID,
                routesURL: routesURL,
                serverID: serverID,
                clientID: clientID,
                eventLogURL: eventLogURL,
                command: nil,
                arguments: []
            )
        }
        throw GatewayInvocationError.missingSeparator
    }
}
