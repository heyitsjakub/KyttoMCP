import Foundation
import Testing
import KyttoCore
@testable import KyttoMCP

@MainActor
struct ProvenanceLookupTests {
    @Test("latest npm metadata is fetched only through the explicit lookup")
    func npmMetadata() async throws {
        let packageName = "@example/server-\(UUID().uuidString.lowercased())"
        let path = "/\(packageName)/latest"
        ProvenanceURLProtocol.install(
            Data(#"{"name":"example","version":"2.3.0"}"#.utf8),
            for: path
        )
        defer { ProvenanceURLProtocol.removePayload(for: path) }

        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [ProvenanceURLProtocol.self]
        let session = URLSession(configuration: configuration)
        let checkedAt = Date(timeIntervalSince1970: 1_800_000_000)
        let lookup = MCPProvenanceLookup(session: session, now: { checkedAt })

        let result = try await lookup.latest(for: MCPProvenance(
            kind: .npm,
            packageName: packageName,
            confidence: "inferred"
        ))

        #expect(result.version == "2.3.0")
        #expect(result.checkedAt == checkedAt)
        #expect(ProvenanceURLProtocol.requestedPaths().contains(path))
    }

    @Test("unknown and non-comparable registry metadata cannot produce a maintenance claim")
    func unsafeMetadataIsRejected() async throws {
        let packageName = "@example/unsafe-\(UUID().uuidString.lowercased())"
        let path = "/\(packageName)/latest"
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [ProvenanceURLProtocol.self]
        let lookup = MCPProvenanceLookup(session: URLSession(configuration: configuration))

        ProvenanceURLProtocol.install(
            Data(#"{"name":"example","version":"next"}"#.utf8),
            for: path
        )
        defer { ProvenanceURLProtocol.removePayload(for: path) }
        await #expect(throws: MCPProvenanceLookup.LookupError.self) {
            try await lookup.latest(for: MCPProvenance(
                kind: .npm,
                packageName: packageName,
                confidence: "inferred"
            ))
        }

        await #expect(throws: MCPProvenanceLookup.LookupError.self) {
            try await lookup.latest(for: MCPProvenance(kind: .unknown))
        }
    }
}

private final class ProvenanceURLProtocol: URLProtocol {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var payloads: [String: Data] = [:]
    nonisolated(unsafe) private static var paths: [String] = []

    static func install(_ payload: Data, for path: String) {
        lock.lock()
        defer { lock.unlock() }
        payloads[path] = payload
    }

    static func removePayload(for path: String) {
        lock.lock()
        defer { lock.unlock() }
        payloads[path] = nil
    }

    static func payload(for path: String) -> Data? {
        lock.lock()
        defer { lock.unlock() }
        return payloads[path]
    }

    static func record(path: String) {
        lock.lock()
        defer { lock.unlock() }
        paths.append(path)
    }

    static func requestedPaths() -> [String] {
        lock.lock()
        defer { lock.unlock() }
        return paths
    }

    override class func canInit(with request: URLRequest) -> Bool {
        request.url?.host == "registry.npmjs.org"
    }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        guard let url = request.url,
              let payload = Self.payload(for: url.path),
              let response = HTTPURLResponse(
                url: url,
                statusCode: 200,
                httpVersion: nil,
                headerFields: ["Content-Length": String(payload.count)]
              ) else {
            client?.urlProtocol(self, didFailWithError: URLError(.badURL))
            return
        }
        Self.record(path: url.path)
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: payload)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}
