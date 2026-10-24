#if os(macOS)
import XCTest
import Foundation
import AppKit
@testable import CleanerCore

final class MacIntegrationTests: XCTestCase {
    func testRealRegistrationThenMissingPathCleanup() throws {
        guard ProcessInfo.processInfo.environment["OWC_RUN_INTEGRATION"] == "1" else {
            throw XCTSkip("Opt-in test modifies only a newly created, uniquely identified test app registration")
        }
        let token = UUID().uuidString.lowercased()
        let folder = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("OpenWithCleaner-CI-\(token)", isDirectory: true)
        let app = folder.appendingPathComponent("Temporary App \(token).app", isDirectory: true)
        let contents = app.appendingPathComponent("Contents", isDirectory: true)
        let macOS = contents.appendingPathComponent("MacOS", isDirectory: true)
        let identifier = "io.github.openwithcleaner.fixture.\(token)"
        try FileManager.default.createDirectory(at: macOS, withIntermediateDirectories: true)
        // No production files or existing application registrations are touched.
        defer {
            _ = try? CommandRunner.run(MacCatalogService.tool, ["-u", app.path])
            try? FileManager.default.removeItem(at: folder)
        }
        let info: [String: Any] = [
            "CFBundleIdentifier": identifier, "CFBundleName": "OpenWithCleaner CI Fixture",
            "CFBundlePackageType": "APPL", "CFBundleExecutable": "fixture", "CFBundleVersion": "1",
            "CFBundleDocumentTypes": [["CFBundleTypeName": "OWC Test", "CFBundleTypeRole": "Viewer",
                "CFBundleTypeExtensions": ["owc-\(token)"]]]
        ]
        let plist = try PropertyListSerialization.data(fromPropertyList: info, format: .xml, options: 0)
        try plist.write(to: contents.appendingPathComponent("Info.plist"))
        let executable = macOS.appendingPathComponent("fixture")
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
        let registered = try CommandRunner.run(MacCatalogService.tool, ["-f", app.path])
        XCTAssertEqual(registered.status, 0, registered.error)
        let service = MacCatalogService()
        let installed = try service.scan()
        let installedRecord = try XCTUnwrap(installed.entries.first { $0.record.path == app.path })
        XCTAssertEqual(installedRecord.status, .installed)
        XCTAssertFalse(installedRecord.canClean)
        try FileManager.default.removeItem(at: app)
        let missing = try service.scan()
        let residual = try XCTUnwrap(missing.entries.first { $0.record.path == app.path })
        XCTAssertTrue(residual.record.systemReportedMissing, "System dump must identify the missing fixture")
        XCTAssertTrue(residual.canClean, residual.reason)
        let summary = try service.cleanup(residual.record)
        defer { try? FileManager.default.removeItem(atPath: summary.snapshotPath) }
        XCTAssertFalse(try service.scan().entries.contains { $0.record.path == app.path })
        XCTAssertTrue(FileManager.default.fileExists(atPath: summary.snapshotPath + "/before.txt"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: summary.snapshotPath + "/after.txt"))
    }

    func testLocalProbeDoesNotTreatSymlinkAsMissing() throws {
        let folder = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("OWC-Probe-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let link = folder.appendingPathComponent("Linked.app")
        try FileManager.default.createSymbolicLink(atPath: link.path, withDestinationPath: "/does-not-exist-\(UUID().uuidString)")
        if case .unknown = LocalPathProbe.inspect(link.path) {} else { XCTFail("Broken symlinks must stay unknown") }
    }

    func testCommandRunnerDrainsBothStreamsAndTimesOut() throws {
        let result = try CommandRunner.run("/usr/bin/python3", ["-c", "import sys; sys.stdout.write('x'*200000); sys.stderr.write('y'*200000)"])
        XCTAssertEqual(result.output.count, 200000); XCTAssertEqual(result.error.count, 200000)
        XCTAssertThrowsError(try CommandRunner.run("/bin/sleep", ["5"], timeout: 0.05))
    }
}
#endif
