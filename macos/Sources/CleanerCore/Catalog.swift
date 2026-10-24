import Foundation

public struct LaunchRecord: Codable, Equatable, Identifiable {
    public var id: String { path }
    public let recordID: String
    public let name: String
    public let path: String
    public let bundleIdentifier: String
    public let version: String
    public let bindings: [String]
    public let systemReportedMissing: Bool
    public let containerMountState: String
    public let containerPath: String
    public var parseIssue: String?

    public init(recordID: String, name: String, path: String, bundleIdentifier: String,
                version: String = "", bindings: [String] = [], systemReportedMissing: Bool = false,
                parseIssue: String? = nil, containerMountState: String = "", containerPath: String = "") {
        self.recordID = recordID; self.name = name; self.path = path
        self.bundleIdentifier = bundleIdentifier; self.version = version
        self.bindings = bindings; self.systemReportedMissing = systemReportedMissing
        self.containerMountState = containerMountState
        self.containerPath = containerPath
        self.parseIssue = parseIssue
    }
}

public enum EntryStatus: String, Codable {
    case installed, missing, unavailable, protected, unknown
    public var label: String {
        switch self {
        case .installed: return "已安装"
        case .missing: return "可清理残留"
        case .unavailable: return "卷或路径待确认"
        case .protected: return "受保护"
        case .unknown: return "待确认"
        }
    }
}

public struct CatalogEntry: Codable, Identifiable, Equatable {
    public var id: String { record.id }
    public let record: LaunchRecord
    public let status: EntryStatus
    public let reason: String
    public let applicationExists: Bool
    public var canClean: Bool { status == .missing }
    public init(record: LaunchRecord, status: EntryStatus, reason: String, applicationExists: Bool = false) {
        self.record = record; self.status = status; self.reason = reason; self.applicationExists = applicationExists
    }
}

public enum PathPresence: Equatable {
    case present, missing, unavailable(String), unknown(String)
}

public struct DumpParseResult {
    public let records: [LaunchRecord]
    public let rejectedBlocks: Int
    public var isRecognized: Bool { !records.isEmpty }
}

public enum DumpParser {
    // lsregister is a system utility, not a stable public serialization format.
    // Unknown or ambiguous fields must never become cleanup instructions.
    public static func parse(_ text: String) -> DumpParseResult {
        var blocks: [[String]] = []; var current: [String] = []
        for line in text.components(separatedBy: .newlines) {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.count >= 8 && trimmed.allSatisfy({ $0 == "-" }) {
                if !current.isEmpty { blocks.append(current); current = [] }
            } else { current.append(line) }
        }
        if !current.isEmpty { blocks.append(current) }
        var records: [LaunchRecord] = []; var rejected = 0
        for block in blocks {
            var fields: [String: [String]] = [:]
            for line in block {
                guard let colon = line.firstIndex(of: ":") else { continue }
                let key = line[..<colon].split(whereSeparator: { $0.isWhitespace }).joined(separator: " ").lowercased()
                let value = String(line[line.index(after: colon)...]).trimmingCharacters(in: .whitespaces)
                fields[key, default: []].append(value)
            }
            guard fields["bundle id"] != nil else { continue }
            guard fields["bundle id"]?.count == 1, fields["path"]?.count == 1,
                  let pathValue = fields["path"]?.first else { rejected += 1; continue }
            let path = removeAddressSuffix(pathValue)
            guard path.hasPrefix("/") else { rejected += 1; continue }
            let identifier = removeAddressSuffix(fields["identifier"]?.first ?? "")
            let rawID = fields["bundle id"]!.first!
            let strippedID = removeAddressSuffix(rawID)
            let recordID: String
            var issue: String?
            if rawID.range(of: #"^.+\s+\(0x[0-9a-fA-F]{1,16}\)$"#, options: .regularExpression) != nil,
                    let hex = rawID.range(of: #"0x[0-9a-fA-F]{1,16}(?=\)$)"#, options: .regularExpression) {
                recordID = String(rawID[hex])
            } else if !strippedID.isEmpty && strippedID.allSatisfy({ $0.isNumber }) { recordID = strippedID }
            else { recordID = strippedID; issue = "无法识别系统记录编号" }
            if fields["identifier", default: []].count != 1 ||
                identifier.range(of: #"^[A-Za-z0-9][A-Za-z0-9._-]*$"#, options: .regularExpression) == nil ||
                identifier.lowercased() == "null" { issue = "应用标识缺失或有歧义" }
            let mountStates = (fields["container mount state"] ?? []) + (fields["mount state"] ?? [])
            if mountStates.count > 1 { issue = "容器挂载状态有歧义" }
            if fields["container", default: []].count > 1 { issue = "容器路径有歧义" }
            let name = [fields["displayname"]?.first, fields["name"]?.first].compactMap { $0 }
                .first { !$0.isEmpty && !["(null)", "null", "#"].contains($0.lowercased()) }
                ?? URL(fileURLWithPath: path).deletingPathExtension().lastPathComponent
            let bindingValues = (fields["bindings"] ?? []) + (fields["claimed utis"] ?? [])
            var bindings = bindingValues.flatMap { value in
                value.split(separator: ",").map { removeAddressSuffix($0.trimmingCharacters(in: .whitespaces)) }
            }.filter { !$0.isEmpty }
            bindings += (fields["claimed schemes"] ?? []).flatMap { $0.split(separator: ",") }
                .map { $0.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: ":", with: "") + ":" }
            records.append(LaunchRecord(recordID: recordID, name: name, path: path,
                bundleIdentifier: identifier, version: String((fields["versionstring"]?.first ?? fields["displayversion"]?.first ?? fields["version"]?.first ?? "").prefix(120)),
                bindings: bindings,
                systemReportedMissing: block.joined(separator: "\n").contains("Bundle node not found on disk"),
                parseIssue: issue, containerMountState: mountStates.first ?? "",
                containerPath: removeAddressSuffix(fields["container"]?.first ?? "")))
        }
        let groups = Dictionary(grouping: records, by: \.path)
        let unique = groups.map { _, samePath -> LaunchRecord in
            var record = samePath[0]
            if samePath.count > 1 { record.parseIssue = "同一路径包含多个系统记录，需要人工确认" }
            return record
        }.sorted { $0.path < $1.path }
        return DumpParseResult(records: unique, rejectedBlocks: rejected)
    }

    private static func removeAddressSuffix(_ value: String) -> String {
        guard let range = value.range(of: #"\s+\(0x[0-9a-fA-F]+\)$"#, options: .regularExpression) else { return value }
        return String(value[..<range.lowerBound])
    }
}

public enum CleanupPolicy {
    public static func pathProblem(_ path: String) -> String? {
        if !path.hasPrefix("/") || path.contains("//") || path.contains("\u{FFFD}") || path.unicodeScalars.contains(where: { $0.value < 32 || $0.value == 127 }) {
            return "不是可安全处理的绝对路径"
        }
        let pieces = path.split(separator: "/", omittingEmptySubsequences: false)
        if pieces.contains("..") || pieces.contains(".") || !path.lowercased().hasSuffix(".app") {
            return "仅处理没有路径歧义的完整 .app 应用包"
        }
        return nil
    }

    public static func classify(_ record: LaunchRecord, presence: PathPresence,
                                defaultBundleIDs: Set<String> = []) -> CatalogEntry {
        func entry(_ status: EntryStatus, _ reason: String) -> CatalogEntry {
            CatalogEntry(record: record, status: status, reason: reason, applicationExists: presence == .present)
        }
        if let issue = record.parseIssue { return entry(.unknown, issue) }
        if let issue = pathProblem(record.path) { return entry(.unknown, issue) }
        let protectedRoots = ["/System", "/Library", "/usr", "/bin", "/sbin"]
        if protectedRoots.contains(where: { record.path == $0 || record.path.hasPrefix($0 + "/") })
            || record.bundleIdentifier.lowercased().hasPrefix("com.apple.") {
            return entry(.protected, "系统位置或 Apple 应用记录，本工具保留")
        }
        if defaultBundleIDs.contains(record.bundleIdentifier.lowercased()) {
            return entry(.protected, "此应用仍有默认打开方式记录，请先在 Finder 的显示简介中更换默认应用")
        }
        if !record.containerMountState.isEmpty && record.containerMountState.lowercased() != "mounted" {
            return entry(.unavailable, "系统记录的容器状态不是已挂载：\(record.containerMountState)")
        }
        if !record.containerPath.isEmpty && !["/", "/System/Volumes/Data"].contains(record.containerPath) {
            return entry(.unavailable, "系统记录指向其他容器或磁盘，本版保留：\(record.containerPath)")
        }
        let uncertainRoots = ["/Volumes", "/Network", "/net", "/home"]
        if uncertainRoots.contains(where: { record.path == $0 || record.path.hasPrefix($0 + "/") }) {
            return entry(.unavailable, "外接盘、磁盘映像或网络路径可能暂时不可用；本版保留此记录")
        }
        switch presence {
        case .present: return entry(.installed, "应用文件仍然存在；此处仅展示，不注销仍在使用的应用")
        case .unavailable(let reason): return entry(.unavailable, reason)
        case .unknown(let reason): return entry(.unknown, reason)
        case .missing:
            if !record.systemReportedMissing { return entry(.unknown, "路径不存在，但系统没有提供明确失效标记；本版保留") }
            return entry(.missing, "系统标记应用节点不存在，并已核对本地路径缺失")
        }
    }

    public static func revalidate(_ planned: LaunchRecord, current: [CatalogEntry]) throws -> CatalogEntry {
        guard let fresh = current.first(where: { $0.record.path == planned.path }) else {
            throw CleanerError.message("此记录已经消失，请刷新列表")
        }
        guard fresh.record.recordID == planned.recordID,
              fresh.record.bundleIdentifier == planned.bundleIdentifier, fresh.canClean else {
            throw CleanerError.message("记录或应用状态已经变化，已取消清理。请重新扫描和预览。")
        }
        return fresh
    }
}

public enum CleanerError: LocalizedError {
    case message(String)
    public var errorDescription: String? { if case .message(let message) = self { return message }; return nil }
}
