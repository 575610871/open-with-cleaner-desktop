#if os(macOS)
import Foundation
import AppKit
import CoreServices
import UniformTypeIdentifiers
import Darwin

public struct CommandResult {
    public let status: Int32
    public let output: String
    public let error: String
}

private final class CapturedOutput {
    let lock = NSLock()
    var data = Data()
    var overflow = false
    func consume(_ handle: FileHandle) {
        while true {
            let chunk = handle.readData(ofLength: 32768)
            if chunk.isEmpty { break }
            lock.lock()
            if data.count + chunk.count <= 64 * 1024 * 1024 { data.append(chunk) }
            else { overflow = true }
            lock.unlock()
        }
    }
}

public enum CommandRunner {
    // Direct executable + argv: application names and paths are never shell code.
    public static func run(_ executable: String, _ arguments: [String], timeout: TimeInterval = 45) throws -> CommandResult {
        let process = Process(); let out = Pipe(); let err = Pipe()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments; process.standardOutput = out; process.standardError = err
        var environment = ProcessInfo.processInfo.environment
        environment["LC_ALL"] = "C"; process.environment = environment
        let finished = DispatchSemaphore(value: 0)
        process.terminationHandler = { _ in finished.signal() }
        try process.run()
        out.fileHandleForWriting.closeFile(); err.fileHandleForWriting.closeFile()
        let output = CapturedOutput(); let errors = CapturedOutput(); let readers = DispatchGroup()
        readers.enter(); DispatchQueue.global().async { output.consume(out.fileHandleForReading); readers.leave() }
        readers.enter(); DispatchQueue.global().async { errors.consume(err.fileHandleForReading); readers.leave() }
        if finished.wait(timeout: .now() + timeout) == .timedOut {
            process.terminate()
            if finished.wait(timeout: .now() + 2) == .timedOut {
                if process.isRunning { Darwin.kill(process.processIdentifier, SIGKILL) }
                _ = finished.wait(timeout: .now() + 2)
            }
            _ = readers.wait(timeout: .now() + 3)
            throw CleanerError.message("系统工具运行超时，已停止本次操作。请刷新后检查结果。")
        }
        guard readers.wait(timeout: .now() + 5) == .success, !output.overflow, !errors.overflow else {
            throw CleanerError.message("系统工具输出过大或无法完整读取，已停止处理")
        }
        return CommandResult(status: process.terminationStatus,
            output: String(decoding: output.data, as: UTF8.self),
            error: String(decoding: errors.data, as: UTF8.self))
    }
}

public enum LocalPathProbe {
    public static func inspect(_ path: String) -> PathPresence {
        guard CleanupPolicy.pathProblem(path) == nil else { return .unknown("路径结构无法确认") }
        var prefix = ""
        let components = path.split(separator: "/")
        for (index, component) in components.enumerated() {
            prefix += "/" + component
            var info = stat()
            let result = prefix.withCString { Darwin.lstat($0, &info) }
            if result == 0 {
                let mode = info.st_mode & mode_t(S_IFMT)
                if mode == mode_t(S_IFLNK) { return .unknown("路径包含符号链接，无法确认目标是否暂时不可用") }
                if index < components.count - 1 && mode != mode_t(S_IFDIR) {
                    return .unknown("路径父项不是目录")
                }
                if index == components.count - 1 { return .present }
                // lstat alone can succeed even if directory search permission is denied.
                if Darwin.access(prefix, X_OK) != 0 { return .unknown("无权访问路径父目录") }
            } else {
                let code = errno
                if code == ENOENT {
                    guard index == components.count - 1 else {
                        return .unknown("应用的父目录也已不存在，无法排除容器或挂载位置变化")
                    }
                    do {
                        let parent = URL(fileURLWithPath: path).deletingLastPathComponent()
                        let volume = try parent.resourceValues(forKeys: [.volumeURLKey, .volumeIsLocalKey,
                            .volumeIsRemovableKey, .volumeIsEjectableKey])
                        guard volume.volumeIsLocal == true,
                              volume.volumeIsRemovable != true, volume.volumeIsEjectable != true,
                              let volumeRoot = volume.volumeURL?.path,
                              ["/", "/System/Volumes/Data"].contains(volumeRoot) else {
                            return .unavailable("应用父目录不在已确认的系统本地卷，本版保留")
                        }
                        return .missing
                    } catch { return .unknown("无法核对应用父目录所在的卷") }
                }
                if code == ENODEV || code == ENXIO || code == ESTALE { return .unavailable("所在卷或网络位置不可用") }
                return .unknown("无法读取路径（系统错误 \(code)），不能认定已经卸载")
            }
        }
        return .unknown("无法确认应用路径")
    }
}

public struct CatalogSnapshot {
    public let entries: [CatalogEntry]
    public let rawDump: String
    public let rejectedBlocks: Int
    public let createdAt: Date
}

public struct FileInspection: Codable {
    public let fileName: String
    public let defaultApplication: String
    public let candidates: [String]
    public init(fileName: String, defaultApplication: String, candidates: [String]) {
        self.fileName = fileName; self.defaultApplication = defaultApplication; self.candidates = candidates
    }
}

public struct OperationSummary: Codable {
    public let message: String
    public let snapshotPath: String
    public init(message: String, snapshotPath: String) { self.message = message; self.snapshotPath = snapshotPath }
}

public final class MacCatalogService {
    public static let tool = "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
    public init() {}

    public func scan() throws -> CatalogSnapshot {
        let started = Date()
        let result = try CommandRunner.run(Self.tool, ["-dump"])
        let dumpedAt = Date()
        guard result.status == 0 else { throw CleanerError.message("无法读取应用注册信息：\(result.error.prefix(1000))") }
        let parsed = DumpParser.parse(result.output)
        let parsedAt = Date()
        guard parsed.isRecognized else {
            throw CleanerError.message("无法识别此 macOS 版本的应用注册输出。未进行清理，请导出系统版本信息并等待兼容更新。")
        }
        let defaultIDs = DefaultProtection.bundleIdentifiers(for: parsed.records)
        let defaultsAt = Date()
        let entries = parsed.records.map { record -> CatalogEntry in
            let preflight = CleanupPolicy.classify(record, presence: .unknown("尚未检查路径"), defaultBundleIDs: defaultIDs)
            if preflight.status == .unavailable { return preflight }
            return CleanupPolicy.classify(record, presence: LocalPathProbe.inspect(record.path), defaultBundleIDs: defaultIDs)
        }.sorted { lhs, rhs in
            if lhs.canClean != rhs.canClean { return lhs.canClean }
            return lhs.record.name.localizedStandardCompare(rhs.record.name) == .orderedAscending
        }
        if ProcessInfo.processInfo.environment["OWC_DIAGNOSTIC_TIMING"] == "1" {
            print("SCAN_TIMING dump=\(dumpedAt.timeIntervalSince(started)) parse=\(parsedAt.timeIntervalSince(dumpedAt)) defaults=\(defaultsAt.timeIntervalSince(parsedAt)) paths=\(Date().timeIntervalSince(defaultsAt)) records=\(entries.count)")
        }
        return CatalogSnapshot(entries: entries, rawDump: result.output,
                               rejectedBlocks: parsed.rejectedBlocks, createdAt: Date())
    }

    public func cleanup(_ planned: LaunchRecord) throws -> OperationSummary {
        let fresh = try scan()
        _ = try CleanupPolicy.revalidate(planned, current: fresh.entries)
        let folder = try OperationStore.createFolder()
        try OperationStore.write(fresh.rawDump, name: "before.txt", folder: folder)
        try OperationStore.writeJSON(planned, name: "selection.json", folder: folder)
        // Recheck after writing the snapshot; any app reappearance blocks mutation.
        guard LocalPathProbe.inspect(planned.path) == .missing else {
            throw CleanerError.message("应用路径状态已经变化，已取消操作。操作记录：\(folder.path)")
        }
        var commandLog = "Only this exact path is requested for unregistering.\n\(planned.path)\n"
        do {
            let result = try CommandRunner.run(Self.tool, ["-u", planned.path])
            commandLog += "Exit: \(result.status)\nSTDOUT:\n\(result.output)\nSTDERR:\n\(result.error)"
            try OperationStore.write(commandLog, name: "command.txt", folder: folder)
            let after = try scan()
            try OperationStore.write(after.rawDump, name: "after.txt", folder: folder)
            guard result.status == 0 else {
                throw CleanerError.message("系统工具未完成注销。操作记录：\(folder.path)")
            }
            guard !after.entries.contains(where: { $0.record.path == planned.path }) else {
                throw CleanerError.message("系统仍返回此路径的注册记录，不能确认清理成功。操作记录：\(folder.path)")
            }
            let summary = OperationSummary(message: "已核对：此路径的注册记录已移除。Finder 菜单可能需要重新打开后更新。", snapshotPath: folder.path)
            try OperationStore.writeJSON(summary, name: "result.json", folder: folder)
            return summary
        } catch {
            try? OperationStore.write(commandLog + "\nERROR:\n" + error.localizedDescription, name: "failure.txt", folder: folder)
            throw error
        }
    }

    public func inspectFile(_ url: URL) -> FileInspection {
        let workspace = NSWorkspace.shared
        let defaultURL = workspace.urlForApplication(toOpen: url)
        let candidates = workspace.urlsForApplications(toOpen: url).map { $0.path }.sorted()
        return FileInspection(fileName: url.lastPathComponent,
            defaultApplication: defaultURL?.path ?? "系统没有返回默认应用", candidates: candidates)
    }

    public func reregister(_ url: URL) throws -> OperationSummary {
        guard url.pathExtension.lowercased() == "app", case .present = LocalPathProbe.inspect(url.path),
              Bundle(url: url)?.bundleIdentifier != nil else {
            throw CleanerError.message("请选择实际存在、可以读取的 .app 应用包")
        }
        let before = try scan(); let folder = try OperationStore.createFolder()
        try OperationStore.write(before.rawDump, name: "before.txt", folder: folder)
        let result = try CommandRunner.run(Self.tool, ["-f", url.path])
        try OperationStore.write("Exit: \(result.status)\n\(result.output)\n\(result.error)", name: "command.txt", folder: folder)
        let after = try scan()
        try OperationStore.write(after.rawDump, name: "after.txt", folder: folder)
        guard result.status == 0, after.entries.contains(where: { $0.record.path == url.path }) else {
            throw CleanerError.message("无法确认应用重新注册成功。操作记录：\(folder.path)")
        }
        return OperationSummary(message: "已重新注册所选应用。此操作不等于恢复旧数据库或默认应用设置。", snapshotPath: folder.path)
    }
}

private enum DefaultProtection {
    static func bundleIdentifiers(for records: [LaunchRecord]) -> Set<String> {
        var identifiers = Set<String>()
        let domains = ["com.apple.LaunchServices/com.apple.launchservices.secure", "com.apple.LaunchServices"]
        for domain in domains {
            if let handlers = CFPreferencesCopyAppValue("LSHandlers" as CFString, domain as CFString) as? [[String: Any]] {
                for handler in handlers {
                    for (key, value) in handler where key.hasPrefix("LSHandlerRole") {
                        if let identifier = value as? String { identifiers.insert(identifier.lowercased()) }
                    }
                }
            }
        }
        let preferenceURL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(
            "Library/Preferences/com.apple.LaunchServices/com.apple.launchservices.secure.plist")
        if let data = try? Data(contentsOf: preferenceURL),
           let plist = try? PropertyListSerialization.propertyList(from: data, options: [], format: nil) as? [String: Any],
           let handlers = plist["LSHandlers"] as? [[String: Any]] {
            for handler in handlers {
                for (key, value) in handler where key.hasPrefix("LSHandlerRole") {
                    if let identifier = value as? String { identifiers.insert(identifier.lowercased()) }
                }
            }
        }
        // Also query effective defaults for each missing record's declared bindings.
        let bindings = Set(records.filter(\.systemReportedMissing).flatMap(\.bindings))
        for binding in bindings {
                var typeID: String?
                if binding.hasPrefix(".") { typeID = UTType(filenameExtension: String(binding.dropFirst()))?.identifier }
                else if binding.contains("/") { typeID = UTType(mimeType: binding)?.identifier }
                else if binding.contains(".") { typeID = binding }
                if let typeID = typeID {
                    for role in [LSRolesMask.all, .viewer, .editor, .shell] {
                        if let value = LSCopyDefaultRoleHandlerForContentType(typeID as CFString, role)?.takeRetainedValue() {
                            identifiers.insert((value as String).lowercased())
                        }
                    }
                }
                if binding.hasSuffix(":") {
                    let scheme = String(binding.dropLast())
                    if let value = LSCopyDefaultHandlerForURLScheme(scheme as CFString)?.takeRetainedValue() {
                        identifiers.insert((value as String).lowercased())
                    }
                }
        }
        return identifiers
    }
}

public enum OperationStore {
    public static var root: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/OpenWithCleanerMac/Operations", isDirectory: true)
    }
    public static func createFolder() throws -> URL {
        let folder = root.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700])
        return folder
    }
    public static func write(_ text: String, name: String, folder: URL) throws {
        let url = folder.appendingPathComponent(name)
        try Data(text.utf8).write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
    public static func writeJSON<T: Encodable>(_ value: T, name: String, folder: URL) throws {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        try write(String(decoding: encoder.encode(value), as: UTF8.self), name: name, folder: folder)
    }
}
#endif
