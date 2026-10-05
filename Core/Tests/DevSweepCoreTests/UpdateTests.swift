import XCTest
@testable import DevSweepCore

final class UpdateTests: XCTestCase {
    func testVersionOrdering() {
        XCTAssertTrue(AppVersion("0.8.2")! < AppVersion("0.8.10")!)
        XCTAssertTrue(AppVersion("v0.9")! > AppVersion("0.8.9")!)
        XCTAssertEqual(AppVersion("1.0")!, AppVersion("1.0.0")!)
        XCTAssertTrue(AppVersion("1.2.3-beta.1")! == AppVersion("1.2.3")!)
        XCTAssertNil(AppVersion("latest"))
    }

    private func json(tag: String, draft: Bool = false, pre: Bool = false) -> Data {
        Data("""
        {"tag_name":"\(tag)","html_url":"https://github.com/karuneshpalekar/DevSweep/releases/tag/\(tag)",
         "body":"Notes","draft":\(draft),"prerelease":\(pre),"published_at":"2026-10-05T10:00:00Z",
         "assets":[{"name":"DevSweep-0.9.0.dmg.sha256","browser_download_url":"https://x/sha"},
                   {"name":"DevSweep-0.9.0.dmg","browser_download_url":"https://x/dmg"}]}
        """.utf8)
    }

    func testNewerReleaseIsAvailable() {
        guard case .available(let info) = UpdateChecker.evaluate(json(tag: "v0.9.0"), current: "0.8.2") else {
            return XCTFail("expected an update")
        }
        XCTAssertEqual(info.version, "0.9.0")
        XCTAssertEqual(info.downloadURL?.absoluteString, "https://x/dmg")
        XCTAssertEqual(info.notes, "Notes")
    }

    func testSameOrOlderIsUpToDate() {
        XCTAssertEqual(UpdateChecker.evaluate(json(tag: "v0.8.2"), current: "0.8.2"), .upToDate(latest: "0.8.2"))
        XCTAssertEqual(UpdateChecker.evaluate(json(tag: "v0.8.0"), current: "0.8.2"), .upToDate(latest: "0.8.0"))
    }

    func testDraftsAndPrereleasesAreIgnored() {
        XCTAssertEqual(UpdateChecker.evaluate(json(tag: "v1.0.0", draft: true), current: "0.8.2"), .upToDate(latest: "0.8.2"))
        XCTAssertEqual(UpdateChecker.evaluate(json(tag: "v1.0.0", pre: true), current: "0.8.2"), .upToDate(latest: "0.8.2"))
    }

    func testGarbageFails() {
        if case .failed = UpdateChecker.evaluate(Data("nope".utf8), current: "0.8.2") {} else { XCTFail() }
    }
}
