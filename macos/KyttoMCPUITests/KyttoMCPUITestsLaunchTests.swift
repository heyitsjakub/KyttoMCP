//
//  KyttoMCPUITestsLaunchTests.swift
//  KyttoMCPUITests
//
//  Created by Jakub Hecht on 30/07/2026.
//

import XCTest

final class KyttoMCPUITestsLaunchTests: XCTestCase {

    override class var runsForEachTargetApplicationUIConfiguration: Bool {
        true
    }

    override func setUpWithError() throws {
        continueAfterFailure = false
    }

    @MainActor
    func testLaunch() throws {
        let (app, _) = try KyttoUITestHome.application(for: self)
        app.launch()

        XCTAssertTrue(app.windows["Kytto"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.buttons["Add server"].waitForExistence(timeout: 5))

        let attachment = XCTAttachment(screenshot: app.screenshot())
        attachment.name = "Launch Screen"
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}
