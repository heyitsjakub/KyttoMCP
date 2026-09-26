import Foundation
import Testing
@testable import KyttoCore

@Suite("Profiles")
struct ProfileStoreTests {

    @Test("profiles survive a round trip and normalize server ids")
    func roundTrip() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let paths = KyttoPaths(home: home.root)
        let store = ProfileStore(paths: paths)

        let created = try store.create(
            name: "  Web development  ",
            serverIDs: ["GitHub", "context7", "github", " "]
        )

        #expect(created.name == "Web development")
        #expect(created.serverIDs == ["context7", "github"])
        #expect(ProfileStore(paths: paths).all() == [created])
    }

    @Test("reading profiles does not create app data")
    func lazyRead() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let paths = KyttoPaths(home: home.root)

        #expect(ProfileStore(paths: paths).all().isEmpty)
        #expect(!FileManager.default.fileExists(atPath: paths.root.path))
    }

    @Test("an empty profile is valid and can represent a minimal setup")
    func emptyProfile() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let profile = try ProfileStore(paths: KyttoPaths(home: home.root))
            .create(name: "Minimal", serverIDs: [])

        #expect(profile.serverIDs.isEmpty)
    }

    @Test("names are unique without caring about case")
    func duplicateNames() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let store = ProfileStore(paths: KyttoPaths(home: home.root))
        _ = try store.create(name: "Coding", serverIDs: [])

        #expect(throws: ProfileError.duplicateName("coding")) {
            try store.create(name: "coding", serverIDs: [])
        }
    }

    @Test("profiles can be updated and deleted")
    func updateAndDelete() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let store = ProfileStore(paths: KyttoPaths(home: home.root))
        let original = try store.create(name: "Coding", serverIDs: ["git"])

        let updated = try store.update(
            id: original.id,
            name: "Research",
            serverIDs: ["fetch", "memory"]
        )
        #expect(updated.id == original.id)
        #expect(updated.serverIDs == ["fetch", "memory"])

        try store.delete(id: original.id)
        #expect(store.all().isEmpty)
    }

    @Test("a malformed profile store is never replaced by a new profile")
    func malformedStoreIsPreserved() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let paths = KyttoPaths(home: home.root)
        let malformed = Data(#"{"unfinished": true"#.utf8)
        try FileManager.default.createDirectory(
            at: paths.profiles.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try malformed.write(to: paths.profiles)

        let store = ProfileStore(paths: paths)
        #expect(store.all().isEmpty)
        #expect(throws: ProfileError.self) {
            try store.create(name: "Do not overwrite", serverIDs: ["github"])
        }
        #expect(try Data(contentsOf: paths.profiles) == malformed)
    }
}
