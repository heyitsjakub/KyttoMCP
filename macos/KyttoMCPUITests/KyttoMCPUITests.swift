//
//  KyttoMCPUITests.swift
//  KyttoMCPUITests
//
//  Created by Jakub Hecht on 30/07/2026.
//

import Foundation
import XCTest

enum KyttoUITestHome {
    static func make(for test: XCTestCase) throws -> URL {
        let home = FileManager.default.temporaryDirectory
            .appending(path: "KyttoMCPUITests-\(UUID().uuidString)", directoryHint: .isDirectory)
        let root = home.appending(path: "Library/Application Support/Kytto", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let settings = """
        {
          "backupRetention" : 20,
          "clientPathOverrides" : {},
          "hasCompletedOnboarding" : true,
          "launchAtLogin" : false,
          "menuBarClient" : null,
          "menuBarEnabled" : false,
          "theme" : "system",
          "tokenWarningThreshold" : 20000
        }
        """
        try settings.write(to: root.appending(path: "settings.json"), atomically: true, encoding: .utf8)
        test.addTeardownBlock { try? FileManager.default.removeItem(at: home) }
        return home
    }

    static func application(for test: XCTestCase) throws -> (XCUIApplication, URL) {
        let home = try make(for: test)
        let app = XCUIApplication()
        app.launchEnvironment["KYTTO_TEST_HOME"] = home.path
        return (app, home)
    }
}

final class KyttoMCPUITests: XCTestCase {

    override func setUpWithError() throws {
        // Put setup code here. This method is called before the invocation of each test method in the class.

        // In UI tests it is usually best to stop immediately when a failure occurs.
        continueAfterFailure = false

        // In UI tests it’s important to set the initial state - such as interface orientation - required for your tests before they run. The setUp method is a good place to do this.
    }

    override func tearDownWithError() throws {
        // Put teardown code here. This method is called after the invocation of each test method in the class.
    }

    @MainActor
    func testAddServerRequiresAClientWithoutTouchingRealConfigs() throws {
        let (app, home) = try KyttoUITestHome.application(for: self)
        app.launch()

        XCTAssertTrue(app.windows["Kytto"].waitForExistence(timeout: 5))
        let toolbarAdd = app.buttons["Add server"].firstMatch
        XCTAssertTrue(toolbarAdd.waitForExistence(timeout: 5))
        toolbarAdd.tap()

        let manual = app.buttons["Start from scratch"]
        XCTAssertTrue(manual.waitForExistence(timeout: 3))
        manual.tap()

        let name = app.textFields.matching(
            NSPredicate(format: "placeholderValue == %@", "github")
        ).firstMatch
        let command = app.textFields.matching(
            NSPredicate(format: "placeholderValue == %@", "npx")
        ).firstMatch
        XCTAssertTrue(name.waitForExistence(timeout: 3))
        XCTAssertTrue(command.exists)
        name.tap()
        name.typeText("ui-test-server")
        command.tap()
        command.typeText("printf")

        // The modal makes the background toolbar inaccessible, so only the
        // form's submit button should remain in the accessibility tree.
        let submit = app.buttons["Add server"].firstMatch
        XCTAssertTrue(submit.waitForExistence(timeout: 3))
        submit.tap()

        XCTAssertTrue(
            app.staticTexts["Choose at least one client to add this server to."]
                .waitForExistence(timeout: 3)
        )
        XCTAssertFalse(FileManager.default.fileExists(atPath: home.appending(path: ".cursor/mcp.json").path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: home.appending(path: ".claude.json").path))
    }

    @MainActor
    func testWindowCanMoveImmediatelyAfterLaunch() throws {
        let (app, _) = try KyttoUITestHome.application(for: self)
        app.launch()

        let window = app.windows["Kytto"]
        XCTAssertTrue(window.waitForExistence(timeout: 5))
        let initialOrigin = window.frame.origin

        let dragStart = window.coordinate(withNormalizedOffset: CGVector(dx: 0.75, dy: 0.04))
        let dragEnd = window.coordinate(withNormalizedOffset: CGVector(dx: 0.85, dy: 0.12))
        dragStart.press(forDuration: 0.1, thenDragTo: dragEnd)

        XCTAssertNotEqual(
            window.frame.origin,
            initialOrigin,
            "The titlebar drag region did not move the window on first launch."
        )
    }

    @MainActor
    func testLaunchPerformance() throws {
        let (app, _) = try KyttoUITestHome.application(for: self)
        measure(metrics: [XCTApplicationLaunchMetric()]) {
            app.launch()
            app.terminate()
        }
    }
}
