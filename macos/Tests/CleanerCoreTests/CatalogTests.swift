import XCTest
@testable import CleanerCore

final class CatalogTests: XCTestCase {
    private let localPath = "/Users/fixture/Applications/Example.app"
    private let identifier = "org.example.cleanerfixture"

    private func record(
        id: String = "42", path: String? = nil, bundleID: String? = nil,
        systemMissing: Bool = true, parseIssue: String? = nil
    ) -> LaunchRecord {
        LaunchRecord(
            recordID: id, name: "Example", path: path ?? localPath,
            bundleIdentifier: bundleID ?? identifier, version: "1.2.3",
            systemReportedMissing: systemMissing, parseIssue: parseIssue
        )
    }

    private func dump(
        id: String = "42", path: String? = nil, bundleID: String? = "org.example.cleanerfixture",
        systemMissing: Bool = true
    ) -> String {
        var lines = [
            "----------------------------------------",
            "bundle id: \(id)",
            "path: \(path ?? localPath)",
            "name: Example",
            "version: 1.2.3"
        ]
        if let bundleID = bundleID { lines.append("identifier: \(bundleID)") }
        if systemMissing { lines.append("Bundle node not found on disk") }
        return lines.joined(separator: "\n")
    }

    private func parseOne(
        _ fixture: String, file: StaticString = #filePath, line: UInt = #line
    ) throws -> LaunchRecord {
        let parsed = DumpParser.parse(fixture)
        XCTAssertEqual(parsed.records.count, 1, file: file, line: line)
        return try XCTUnwrap(parsed.records.first, file: file, line: line)
    }

    func testParsesSpacePaddedBundleRecordAndBindings() throws {
        let fixture = """
        ----------------------------------------
        bundle id:       42
        path:            /Users/fixture/Applications/Example.app
        identifier:      org.example.cleanerfixture
        name:            Internal name
        displayname:     Example Display
        version:         1.2.3
        bindings:        public.text, .txt,  org.example.document
        Bundle node not found on disk
        """
        let parsed = try parseOne(fixture)
        XCTAssertEqual(parsed.recordID, "42")
        XCTAssertEqual(parsed.path, localPath)
        XCTAssertEqual(parsed.bundleIdentifier, identifier)
        XCTAssertEqual(parsed.name, "Example Display")
        XCTAssertEqual(parsed.version, "1.2.3")
        XCTAssertEqual(parsed.bindings, ["public.text", ".txt", "org.example.document"])
        XCTAssertTrue(parsed.systemReportedMissing)
        XCTAssertNil(parsed.parseIssue)
    }

    func testParsesTabsAndNormalizesFieldWhitespace() throws {
        let fixture = "--------\n\tbundle\tid:\t42\n\tpath:\t\(localPath)\n\tidentifier:\t\(identifier)\n\tname:\tTabbed App\n\tBundle node not found on disk"
        let parsed = try parseOne(fixture)
        XCTAssertEqual(parsed.recordID, "42")
        XCTAssertEqual(parsed.path, localPath)
        XCTAssertEqual(parsed.bundleIdentifier, identifier)
        XCTAssertEqual(parsed.name, "Tabbed App")
        XCTAssertTrue(parsed.systemReportedMissing)
    }

    func testStripsAddressAnnotationsFromIdentityAndPath() throws {
        let fixture = """
        --------
        bundle id: 42 (0xABcd1234)
        path: /Users/fixture/Applications/Example.app (0x000A)
        identifier: org.example.cleanerfixture (0xdeadBEEF)
        Bundle node not found on disk
        """
        let parsed = try parseOne(fixture)
        XCTAssertEqual(parsed.recordID, "42")
        XCTAssertEqual(parsed.path, localPath)
        XCTAssertEqual(parsed.bundleIdentifier, identifier)
        XCTAssertNil(parsed.parseIssue)
    }

    func testMissingMarkerBeforeBundleIDRemainsInSameRecord() throws {
        let fixture = """
        ----------------------------------------
        Bundle node not found on disk
        bundle id: 42
        path: /Users/fixture/Applications/Example.app
        identifier: org.example.cleanerfixture
        """
        let parsed = try parseOne(fixture)
        XCTAssertTrue(parsed.systemReportedMissing)
        XCTAssertTrue(CleanupPolicy.classify(parsed, presence: .missing).canClean)
    }

    func testClaimBlockCannotBecomeApplicationRecord() {
        let fixture = """
        ----------------------------------------
        claim id: 42
        path: /Users/fixture/Applications/Example.app
        identifier: org.example.cleanerfixture
        Bundle node not found on disk
        """
        let parsed = DumpParser.parse(fixture)
        XCTAssertFalse(parsed.isRecognized)
        XCTAssertTrue(parsed.records.isEmpty)
    }

    func testClaimMarkerCannotMakeAdjacentBundleMissing() throws {
        let fixture = """
        claim id: 12
        path: /Users/fixture/Applications/ClaimOnly.app
        Bundle node not found on disk
        """ + "\n" + dump(systemMissing: false)
        let parsed = try parseOne(fixture)
        XCTAssertFalse(parsed.systemReportedMissing)
        XCTAssertFalse(CleanupPolicy.classify(parsed, presence: .missing).canClean)
    }

    func testPathWithColonSpacesAndChineseIsPreserved() throws {
        let path = "/Users/fixture/Applications/演示: 版本 二.app"
        let parsed = try parseOne(dump(path: path))
        XCTAssertEqual(parsed.path, path)
        XCTAssertTrue(CleanupPolicy.classify(parsed, presence: .missing).canClean)
    }

    func testFallbackNameComesFromBundleFilename() throws {
        let fixture = "bundle id: 42\npath: /Users/fixture/Applications/演示 工具.app\nidentifier: org.example.fixture\nBundle node not found on disk"
        XCTAssertEqual(try parseOne(fixture).name, "演示 工具")
    }

    func testEmptyAndUnrecognizedDumpsAreNotRecognized() {
        for fixture in ["", "Launch Services: unavailable", "claim id: 42\nname: Example"] {
            let parsed = DumpParser.parse(fixture)
            XCTAssertFalse(parsed.isRecognized)
            XCTAssertTrue(parsed.records.isEmpty)
        }
    }

    func testMissingIdentifierBecomesUnknownInsteadOfCleanable() throws {
        let parsed = try parseOne(dump(bundleID: nil))
        XCTAssertNotNil(parsed.parseIssue)
        let entry = CleanupPolicy.classify(parsed, presence: .missing)
        XCTAssertEqual(entry.status, .unknown)
        XCTAssertFalse(entry.canClean)
    }

    func testEmptyIdentifierBecomesUnknownInsteadOfCleanable() throws {
        let parsed = try parseOne(dump(bundleID: ""))
        XCTAssertNotNil(parsed.parseIssue)
        XCTAssertFalse(CleanupPolicy.classify(parsed, presence: .missing).canClean)
    }

    func testMultipleIdentifiersCannotBeCleaned() throws {
        let parsed = try parseOne(dump() + "\nidentifier: org.example.another")
        XCTAssertNotNil(parsed.parseIssue)
        XCTAssertEqual(CleanupPolicy.classify(parsed, presence: .missing).status, .unknown)
    }

    func testDuplicatePathFieldsRejectEntireBlock() {
        let parsed = DumpParser.parse(dump() + "\npath: /Users/fixture/Applications/Another.app")
        XCTAssertEqual(parsed.rejectedBlocks, 1)
        XCTAssertTrue(parsed.records.isEmpty)
    }

    func testDuplicateBundleNumberFieldsRejectEntireBlock() {
        let parsed = DumpParser.parse(dump() + "\nbundle id: 99")
        XCTAssertEqual(parsed.rejectedBlocks, 1)
        XCTAssertTrue(parsed.records.isEmpty)
    }

    func testMissingPathRejectsBundleBlock() {
        let parsed = DumpParser.parse("bundle id: 42\nidentifier: org.example.fixture")
        XCTAssertEqual(parsed.rejectedBlocks, 1)
        XCTAssertTrue(parsed.records.isEmpty)
    }

    func testInvalidBundleNumbersBecomeUnknown() throws {
        for id in ["", "abc", "42x", "-42", "42 43", "0x42"] {
            let parsed = try parseOne(dump(id: id))
            XCTAssertNotNil(parsed.parseIssue, "Unexpected accepted record number: \(id)")
            XCTAssertFalse(CleanupPolicy.classify(parsed, presence: .missing).canClean)
        }
    }

    func testMultipleRecordsAtSamePathAreAmbiguousAndCannotBeCleaned() throws {
        let parsed = DumpParser.parse(dump(id: "42") + "\n" + dump(id: "99"))
        XCTAssertEqual(parsed.records.count, 1)
        let merged = try XCTUnwrap(parsed.records.first)
        XCTAssertNotNil(merged.parseIssue)
        let entry = CleanupPolicy.classify(merged, presence: .missing)
        XCTAssertEqual(entry.status, .unknown)
        XCTAssertFalse(entry.canClean)
        XCTAssertThrowsError(try CleanupPolicy.revalidate(record(), current: [entry]))
    }

    func testDifferentAppPathsRemainSeparateRecords() {
        let another = "/Users/fixture/Applications/Another.app"
        let parsed = DumpParser.parse(dump() + "\n" + dump(id: "99", path: another))
        XCTAssertEqual(parsed.records.count, 2)
        XCTAssertTrue(parsed.records.allSatisfy { $0.parseIssue == nil })
        XCTAssertEqual(Set(parsed.records.map(\.path)), Set([localPath, another]))
    }

    func testNonAppPathsCannotBeCleaned() {
        for path in ["/Users/fixture/Example.txt", "/Users/fixture/Example.app/Contents/Info.plist", "/Users/fixture/Example.app/"] {
            XCTAssertNotNil(CleanupPolicy.pathProblem(path))
            XCTAssertFalse(CleanupPolicy.classify(record(path: path), presence: .missing).canClean)
        }
    }

    func testRelativePathsAreRejectedByParserAndPolicy() {
        let path = "Applications/Example.app"
        XCTAssertTrue(DumpParser.parse(dump(path: path)).records.isEmpty)
        XCTAssertNotNil(CleanupPolicy.pathProblem(path))
        XCTAssertFalse(CleanupPolicy.classify(record(path: path), presence: .missing).canClean)
    }

    func testTraversalAndAmbiguousSeparatorsCannotBeCleaned() {
        for path in ["/Users/fixture/../Example.app", "/Users/fixture/./Example.app", "/Users//fixture/Example.app"] {
            XCTAssertNotNil(CleanupPolicy.pathProblem(path))
            XCTAssertEqual(CleanupPolicy.classify(record(path: path), presence: .missing).status, .unknown)
        }
    }

    func testControlCharactersCannotBecomeCleanupPaths() {
        for control in ["\u{0000}", "\t", "\n", "\r", "\u{001B}", "\u{007F}"] {
            let path = "/Users/fixture/Example\(control).app"
            XCTAssertNotNil(CleanupPolicy.pathProblem(path))
            XCTAssertFalse(CleanupPolicy.classify(record(path: path), presence: .missing).canClean)
        }
    }

    func testSystemDirectoriesAreProtectedEvenWhenMissing() {
        for path in ["/System/Applications/Example.app", "/Library/Example.app", "/usr/local/Example.app", "/bin/Example.app", "/sbin/Example.app"] {
            let entry = CleanupPolicy.classify(record(path: path), presence: .missing)
            XCTAssertEqual(entry.status, .protected)
            XCTAssertFalse(entry.canClean)
        }
    }

    func testProtectionUsesDirectoryBoundaries() {
        let entry = CleanupPolicy.classify(record(path: "/LibraryExtras/Example.app"), presence: .missing)
        XCTAssertTrue(entry.canClean)
    }

    func testAppleBundleIdentifiersAreProtectedCaseInsensitively() {
        for bundleID in ["com.apple.fixture", "COM.APPLE.Fixture"] {
            let entry = CleanupPolicy.classify(record(bundleID: bundleID), presence: .missing)
            XCTAssertEqual(entry.status, .protected)
            XCTAssertFalse(entry.canClean)
        }
    }

    func testDefaultApplicationBundleIdentifierIsProtected() {
        let entry = CleanupPolicy.classify(record(), presence: .missing, defaultBundleIDs: [identifier])
        XCTAssertEqual(entry.status, .protected)
        XCTAssertFalse(entry.canClean)
    }

    func testDefaultApplicationMatchingNormalizesRecordIdentifier() {
        let entry = CleanupPolicy.classify(record(bundleID: identifier.uppercased()), presence: .missing, defaultBundleIDs: [identifier])
        XCTAssertEqual(entry.status, .protected)
        XCTAssertFalse(entry.canClean)
    }

    func testExternalDiskImageAndNetworkPathsAreRetained() {
        for path in ["/Volumes/Fixture Disk/Example.app", "/Network/Fixture/Example.app", "/net/Fixture/Example.app", "/home/fixture/Example.app"] {
            let entry = CleanupPolicy.classify(record(path: path), presence: .missing)
            XCTAssertEqual(entry.status, .unavailable)
            XCTAssertFalse(entry.canClean)
        }
    }

    func testUnavailablePathRetainsReasonAndCannotBeCleaned() {
        let entry = CleanupPolicy.classify(record(), presence: .unavailable("虚构卷当前未挂载"))
        XCTAssertEqual(entry.status, .unavailable)
        XCTAssertEqual(entry.reason, "虚构卷当前未挂载")
        XCTAssertFalse(entry.canClean)
    }

    func testUnknownPermissionResultCannotBeCleaned() {
        let entry = CleanupPolicy.classify(record(), presence: .unknown("虚构目录权限无法确认"))
        XCTAssertEqual(entry.status, .unknown)
        XCTAssertEqual(entry.reason, "虚构目录权限无法确认")
        XCTAssertFalse(entry.canClean)
    }

    func testInstalledApplicationCannotBeCleanedDespiteSystemMissingMarker() {
        let entry = CleanupPolicy.classify(record(systemMissing: true), presence: .present)
        XCTAssertEqual(entry.status, .installed)
        XCTAssertFalse(entry.canClean)
    }

    func testMissingPathWithoutSystemMissingMarkerIsRetained() {
        let entry = CleanupPolicy.classify(record(systemMissing: false), presence: .missing)
        XCTAssertEqual(entry.status, .unknown)
        XCTAssertFalse(entry.canClean)
    }

    func testOnlyConfirmedSystemAndLocalMissingStateIsCleanable() {
        let entry = CleanupPolicy.classify(record(systemMissing: true), presence: .missing)
        XCTAssertEqual(entry.status, .missing)
        XCTAssertTrue(entry.canClean)
    }

    func testParseIssueBlocksOtherwiseMissingRecord() {
        let entry = CleanupPolicy.classify(record(parseIssue: "虚构格式不明确"), presence: .missing)
        XCTAssertEqual(entry.status, .unknown)
        XCTAssertEqual(entry.reason, "虚构格式不明确")
        XCTAssertFalse(entry.canClean)
    }

    func testRevalidationAcceptsUnchangedConfirmedMissingRecord() throws {
        let planned = record()
        let fresh = CleanupPolicy.classify(planned, presence: .missing)
        XCTAssertEqual(try CleanupPolicy.revalidate(planned, current: [fresh]), fresh)
    }

    func testReinstallationAfterPreviewCancelsCleanup() {
        let planned = record()
        let reinstalled = CleanupPolicy.classify(planned, presence: .present)
        XCTAssertThrowsError(try CleanupPolicy.revalidate(planned, current: [reinstalled]))
    }

    func testChangedRecordNumberAfterPreviewCancelsCleanup() {
        let fresh = CleanupPolicy.classify(record(id: "99"), presence: .missing)
        XCTAssertThrowsError(try CleanupPolicy.revalidate(record(), current: [fresh]))
    }

    func testChangedBundleIdentifierAfterPreviewCancelsCleanup() {
        let fresh = CleanupPolicy.classify(record(bundleID: "org.example.replacement"), presence: .missing)
        XCTAssertThrowsError(try CleanupPolicy.revalidate(record(), current: [fresh]))
    }

    func testDisappearedRecordAfterPreviewCancelsCleanup() {
        XCTAssertThrowsError(try CleanupPolicy.revalidate(record(), current: []))
    }

    func testChangedPathAfterPreviewCancelsCleanup() {
        let moved = CleanupPolicy.classify(record(path: "/Users/fixture/Applications/Moved.app"), presence: .missing)
        XCTAssertThrowsError(try CleanupPolicy.revalidate(record(), current: [moved]))
    }

    func testNewDefaultAssociationAfterPreviewCancelsCleanup() {
        let fresh = CleanupPolicy.classify(record(), presence: .missing, defaultBundleIDs: [identifier])
        XCTAssertThrowsError(try CleanupPolicy.revalidate(record(), current: [fresh]))
    }

    func testRemovedSystemMissingMarkerAfterPreviewCancelsCleanup() {
        let fresh = CleanupPolicy.classify(record(systemMissing: false), presence: .missing)
        XCTAssertThrowsError(try CleanupPolicy.revalidate(record(), current: [fresh]))
    }
}
