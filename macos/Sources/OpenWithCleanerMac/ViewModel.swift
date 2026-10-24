import SwiftUI
import AppKit
import CleanerCore
import UniformTypeIdentifiers
import Darwin

@MainActor
final class CleanerViewModel: ObservableObject {
    @Published var records: [CatalogEntry] = []
    @Published var selectedID: String?
    @Published var query = ""
    @Published var showInstalled = false
    @Published var isBusy = false
    @Published var statusMessage = "扫描应用记录以开始"
    @Published var errorMessage: String?
    @Published var fileInspection: FileInspection?
    @Published var lastOperation: OperationSummary?
    private var lastScan: CatalogSnapshot?

    var visibleRecords: [CatalogEntry] {
        records.filter { entry in
            if !showInstalled && entry.applicationExists { return false }
            let text = [entry.record.name, entry.record.path, entry.record.bundleIdentifier].joined(separator: " ")
            return query.isEmpty || text.localizedCaseInsensitiveContains(query)
        }
    }
    var selectedRecord: CatalogEntry? {
        visibleRecords.first { $0.id == selectedID }
    }

    func scan() {
        guard !isBusy else { return }
        isBusy = true; statusMessage = "正在读取系统注册信息并检查应用路径…"
        Task {
            do {
                let snapshot = try await Task.detached(priority: .userInitiated) { try MacCatalogService().scan() }.value
                apply(snapshot)
            } catch { errorMessage = error.localizedDescription; statusMessage = "扫描未完成，未进行清理" }
            isBusy = false
        }
    }

    private func apply(_ snapshot: CatalogSnapshot) {
        lastScan = snapshot; records = snapshot.entries
        if selectedRecord == nil { selectedID = visibleRecords.first?.id }
        let count = records.filter(\.canClean).count
        statusMessage = "扫描完成 · \(count) 项可清理 · \(snapshot.rejectedBlocks) 个无法识别的记录块已跳过"
    }

    func previewCleanup() {
        guard !isBusy, let entry = selectedRecord, entry.canClean else { return }
        let alert = NSAlert()
        alert.messageText = "清理这一条失效应用记录？"
        alert.informativeText = "应用：\(entry.record.name)\n路径：\(entry.record.path)\n标识：\(entry.record.bundleIdentifier)\n\n将保存操作前后的诊断快照，并仅注销此路径的记录。不会删除应用文件。快照不是完整数据库备份，不能用于恢复已不存在的应用。执行前会再次检查路径和默认关联。"
        alert.alertStyle = .warning
        alert.addButton(withTitle: "保存记录并清理"); alert.addButton(withTitle: "取消")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        perform("正在保存操作记录并清理…") { try MacCatalogService().cleanup(entry.record) }
    }

    private func perform(_ message: String, operation: @escaping () throws -> OperationSummary) {
        guard !isBusy else { return }
        isBusy = true; statusMessage = message
        Task {
            do {
                let summary = try await Task.detached(priority: .userInitiated) { try operation() }.value
                lastOperation = summary
                let snapshot = try await Task.detached { try MacCatalogService().scan() }.value
                apply(snapshot); statusMessage = summary.message
            } catch { errorMessage = error.localizedDescription; statusMessage = "操作未完成，请刷新后核对" }
            isBusy = false
        }
    }

    func chooseFile() {
        guard !isBusy else { return }
        let panel = NSOpenPanel(); panel.canChooseDirectories = false; panel.allowsMultipleSelection = false
        panel.message = "选择一个实际文件，检查系统返回的默认应用和打开候选。"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        fileInspection = MacCatalogService().inspectFile(url)
        statusMessage = "已读取所选文件的打开候选，未修改默认应用"
    }

    func exportReport() {
        guard !isBusy, let snapshot = lastScan else { return }
        let panel = NSSavePanel(); panel.nameFieldStringValue = "OpenWithCleaner-macOS-report.txt"
        panel.message = "报告包含应用路径和标识符，分享前请检查内容。"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        var report = "Open With Cleaner Desktop Suoh Edition · macOS 0.1.0\n"
        report += "OS: \(ProcessInfo.processInfo.operatingSystemVersionString)\nDate: \(snapshot.createdAt)\n"
        report += "Parsed: \(records.count); skipped blocks: \(snapshot.rejectedBlocks)\n\n"
        for entry in records {
            report += "[\(entry.status.label)] \(entry.record.name)\n\(entry.record.path)\n\(entry.record.bundleIdentifier)\n\(entry.reason)\n\n"
        }
        do { try Data(report.utf8).write(to: url, options: .atomic); statusMessage = "诊断报告已保存" }
        catch { errorMessage = error.localizedDescription }
    }

    func revealSelected() {
        guard let entry = selectedRecord, entry.applicationExists else { return }
        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: entry.record.path)])
    }

    func openBackupFolder() {
        do {
            try FileManager.default.createDirectory(at: OperationStore.root, withIntermediateDirectories: true,
                attributes: [.posixPermissions: 0o700])
            NSWorkspace.shared.open(OperationStore.root)
        } catch { errorMessage = error.localizedDescription }
    }

    func reregisterApplication() {
        guard !isBusy else { return }
        let panel = NSOpenPanel(); panel.allowedContentTypes = [.applicationBundle]
        panel.canChooseDirectories = false; panel.treatsFilePackagesAsDirectories = false
        panel.message = "选择已经重新安装的 .app，让系统重新发现它。此操作不恢复旧默认应用设置。"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        perform("正在重新注册所选应用…") { try MacCatalogService().reregister(url) }
    }

    func restartFinder() {
        guard !isBusy else { return }
        let alert = NSAlert(); alert.messageText = "重新启动 Finder？"
        alert.informativeText = "Finder 窗口和桌面会短暂重新载入。请先完成正在进行的文件复制或移动。此操作只刷新界面，不能保证移除所有候选。"
        alert.addButton(withTitle: "重新启动 Finder"); alert.addButton(withTitle: "取消")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        // Explicit, separate user action; never performed as part of cleanup.
        for application in NSRunningApplication.runningApplications(withBundleIdentifier: "com.apple.finder") {
            application.terminate()
        }
        let configuration = NSWorkspace.OpenConfiguration()
        NSWorkspace.shared.openApplication(at: URL(fileURLWithPath: "/System/Library/CoreServices/Finder.app"),
            configuration: configuration) { _, _ in }
        statusMessage = "已请求重新启动 Finder，请重新打开文件菜单检查"
    }

    func runSmokeTestAndExit() async {
        do {
            let snapshot = try await Task.detached { try MacCatalogService().scan() }.value
            apply(snapshot)
            guard !records.isEmpty else { throw CleanerError.message("No application records") }
            if let screenshotPath = ProcessInfo.processInfo.environment["OWC_SCREENSHOT_PATH"] {
                selectedID = nil
                NSApp.activate(ignoringOtherApps: true)
                try await Task.sleep(nanoseconds: 700_000_000)
                guard let view = NSApp.windows.first(where: { $0.isVisible })?.contentView,
                      let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else {
                    throw CleanerError.message("Cannot capture native app preview")
                }
                view.cacheDisplay(in: view.bounds, to: bitmap)
                guard let image = bitmap.representation(using: .png, properties: [:]) else {
                    throw CleanerError.message("Cannot encode native app preview")
                }
                try image.write(to: URL(fileURLWithPath: screenshotPath), options: .atomic)
            }
            print("SMOKE_OK: native UI started; read-only scan returned \(records.count) records")
            fflush(stdout); Darwin.exit(0)
        } catch {
            fputs("SMOKE_FAILED: \(error.localizedDescription)\n", stderr); Darwin.exit(1)
        }
    }
}
