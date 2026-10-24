import SwiftUI
import CleanerCore

enum SuohTheme {
    static let accent = Color(red: 153.0 / 255.0, green: 44.0 / 255.0, blue: 73.0 / 255.0)
    static let canvas = Color(red: 252.0 / 255.0, green: 249.0 / 255.0, blue: 247.0 / 255.0)
    static let rose = Color(red: 247.0 / 255.0, green: 234.0 / 255.0, blue: 237.0 / 255.0)
    static let ink = Color(red: 48.0 / 255.0, green: 40.0 / 255.0, blue: 43.0 / 255.0)
}

@MainActor
struct CleanerMainView: View {
    @ObservedObject var model: CleanerViewModel

    var body: some View {
        VStack(spacing: 0) {
            NavigationSplitView {
                sidebar
                    .navigationSplitViewColumnWidth(min: 285, ideal: 320, max: 420)
            } detail: {
                detail
            }
            Divider()
            statusBar
        }
        .background(SuohTheme.canvas)
        .preferredColorScheme(.light)
        .toolbar {
            ToolbarItemGroup(placement: .automatic) {
                Button(action: model.scan) {
                    Label("扫描记录", systemImage: "arrow.clockwise")
                }
                .help("重新读取注册记录并检查程序路径")
                .disabled(model.isBusy)

                Button(action: model.chooseFile) {
                    Label("检查文件…", systemImage: "doc.text.magnifyingglass")
                }
                .help("选择一个实际文件，查看系统提供的打开候选")
                .disabled(model.isBusy)

                Menu {
                    Button("导出诊断报告…", action: model.exportReport)
                        .disabled(model.isBusy || model.records.isEmpty)
                    Button("打开操作记录", action: model.openBackupFolder)
                    Divider()
                    Button("重新注册应用…", action: model.reregisterApplication)
                        .disabled(model.isBusy)
                    Button("重新启动 Finder…", action: model.restartFinder)
                        .disabled(model.isBusy)
                } label: {
                    Label("更多", systemImage: "ellipsis.circle")
                }
                .help("诊断、操作记录与恢复应用发现")
            }
        }
        .alert("操作未完成", isPresented: Binding(
            get: { model.errorMessage != nil },
            set: { if !$0 { model.errorMessage = nil } }
        )) {
            Button("好", role: .cancel) { model.errorMessage = nil }
        } message: {
            Text(model.errorMessage ?? "")
        }
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 7) {
                HStack(spacing: 9) {
                    Image(systemName: "app.badge.checkmark")
                        .font(.system(size: 25, weight: .medium))
                        .foregroundStyle(SuohTheme.accent)
                    VStack(alignment: .leading, spacing: 3) {
                        Text("Open With Cleaner")
                            .font(.system(size: 18, weight: .semibold))
                            .foregroundStyle(SuohTheme.ink)
                        Text("DESKTOP SUOH EDITION · macOS")
                            .font(.system(size: 9, weight: .semibold, design: .rounded))
                            .tracking(0.8)
                            .foregroundStyle(SuohTheme.accent)
                    }
                }
                Text("让打开方式列表清楚一些。")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .padding(.top, 3)
                HStack(spacing: 7) {
                    Image(systemName: "magnifyingglass")
                        .foregroundStyle(.secondary)
                    TextField("搜索应用、路径或标识符", text: $model.query)
                        .textFieldStyle(.plain)
                        .accessibilityLabel("搜索应用记录")
                    if !model.query.isEmpty {
                        Button { model.query = "" } label: {
                            Image(systemName: "xmark.circle.fill")
                                .foregroundStyle(.secondary)
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel("清除搜索")
                    }
                }
                .padding(9)
                .background(.white.opacity(0.8), in: RoundedRectangle(cornerRadius: 8))
                .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(SuohTheme.accent.opacity(0.12)))
                .padding(.top, 9)

                Toggle("显示已安装应用", isOn: $model.showInstalled)
                    .toggleStyle(.checkbox)
                    .font(.callout)
                    .padding(.top, 5)
                Text("默认查看失效、待确认及受保护的记录。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .padding(18)

            Divider().padding(.horizontal, 12)

            if model.visibleRecords.isEmpty {
                sidebarEmpty
            } else {
                List(selection: $model.selectedID) {
                    ForEach(model.visibleRecords) { entry in
                        recordRow(entry)
                            .tag(entry.id)
                    }
                }
                .listStyle(.sidebar)
                .scrollContentBackground(.hidden)
            }
        }
        .background(SuohTheme.canvas)
    }

    private var sidebarEmpty: some View {
        VStack(spacing: 12) {
            Image(systemName: model.isBusy ? "hourglass" : "checkmark.circle")
                .font(.system(size: 28, weight: .light))
                .foregroundStyle(SuohTheme.accent.opacity(0.7))
            Text(model.isBusy ? "正在检查应用记录…" : "没有匹配的记录")
                .font(.headline)
            Text(model.query.isEmpty
                 ? "扫描完成后，可在这里查看需要检查的应用；勾选上方选项查看已安装应用。"
                 : "试试其他应用名称、路径或标识符。")
                .font(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(26)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func recordRow(_ entry: CatalogEntry) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: entry.canClean ? "app.dashed" : "app")
                .font(.system(size: 24, weight: .light))
                .foregroundStyle(entry.canClean ? SuohTheme.accent : .secondary)
                .frame(width: 30, height: 32)
            VStack(alignment: .leading, spacing: 4) {
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text(entry.record.name)
                        .font(.system(size: 13, weight: .medium))
                        .lineLimit(1)
                    Spacer(minLength: 0)
                    Text(entry.status.label)
                        .font(.system(size: 10, weight: .medium))
                        .foregroundStyle(statusColor(entry.status))
                }
                Text(entry.record.path)
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                    .truncationMode(.middle)
            }
        }
        .padding(.vertical, 5)
        .help(entry.record.path)
    }

    private var detail: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                if let entry = model.selectedRecord {
                    recordDetail(entry)
                } else {
                    welcome
                }

                if let inspection = model.fileInspection {
                    fileInspectionCard(inspection)
                }

                if let operation = model.lastOperation {
                    card {
                        Label("最近一次操作", systemImage: "clock.arrow.circlepath")
                            .font(.headline)
                        Text(operation.message)
                            .font(.callout)
                            .fixedSize(horizontal: false, vertical: true)
                        Text(operation.snapshotPath)
                            .font(.caption.monospaced())
                            .foregroundStyle(.secondary)
                            .textSelection(.enabled)
                        Button("打开操作记录", action: model.openBackupFolder)
                    }
                }

                HStack(alignment: .top, spacing: 8) {
                    Image(systemName: "info.circle")
                        .foregroundStyle(SuohTheme.accent)
                    Text("清理仅处理确认失效的本地应用记录，不删除应用文件。系统候选与 Finder 菜单可能存在更新延迟。")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .padding(28)
            .frame(maxWidth: 850, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .topLeading)
        }
        .background(Color.white)
    }

    private var welcome: some View {
        VStack(alignment: .leading, spacing: 20) {
            Image(systemName: "square.stack.3d.up")
                .font(.system(size: 43, weight: .ultraLight))
                .foregroundStyle(SuohTheme.accent)
                .padding(.top, 12)
            VStack(alignment: .leading, spacing: 9) {
                Text("整理「打开方式」")
                    .font(.system(size: 28, weight: .semibold))
                    .foregroundStyle(SuohTheme.ink)
                Text("先确认记录来自哪里，再处理已经失效的项目。")
                    .font(.title3)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            card {
                walkthrough("1", title: "扫描应用记录", text: "读取系统注册信息，逐条检查应用路径。扫描不会清理记录。")
                Divider()
                walkthrough("2", title: "检查需要处理的项目", text: "从左侧选择记录，查看实际路径和判定原因。外接卷、权限问题及系统项目会保留。")
                Divider()
                walkthrough("3", title: "预览后再清理", text: "只有确认失效的本地记录可清理。执行前会展示具体项目并保存操作记录。")
            }

            Button(action: model.chooseFile) {
                Label("检查某个文件的打开方式…", systemImage: "doc.text.magnifyingglass")
            }
            .disabled(model.isBusy)
            Text("选择实际文件，查看系统给出的默认应用与候选。")
                .font(.caption)
                .foregroundStyle(.secondary)
                .padding(.top, -12)
        }
    }

    @ViewBuilder
    private func recordDetail(_ entry: CatalogEntry) -> some View {
        HStack(alignment: .center, spacing: 16) {
            Image(systemName: entry.canClean ? "app.dashed" : "app")
                .font(.system(size: 42, weight: .ultraLight))
                .foregroundStyle(SuohTheme.accent)
                .frame(width: 66, height: 66)
                .background(SuohTheme.rose, in: RoundedRectangle(cornerRadius: 15))
            VStack(alignment: .leading, spacing: 6) {
                Text(entry.record.name)
                    .font(.system(size: 25, weight: .semibold))
                    .textSelection(.enabled)
                Text(entry.status.label)
                    .font(.callout.weight(.medium))
                    .foregroundStyle(statusColor(entry.status))
            }
        }

        card {
            metadata("应用路径", value: entry.record.path, monospaced: true)
            Divider()
            metadata("应用标识符", value: entry.record.bundleIdentifier.isEmpty ? "未提供" : entry.record.bundleIdentifier, monospaced: true)
            Divider()
            metadata("版本", value: entry.record.version.isEmpty ? "未提供" : entry.record.version)
        }

        card {
            Label("检查结果", systemImage: entry.canClean ? "checkmark.seal" : "info.circle")
                .font(.headline)
                .foregroundStyle(SuohTheme.accent)
            Text(entry.reason)
                .font(.callout)
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)

            if entry.applicationExists {
                Text("应用文件仍然存在，系统可以再次注册它。本版不会将已安装应用永久隐藏。")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }

        HStack(spacing: 12) {
            Button(action: model.previewCleanup) {
                Label("预览清理…", systemImage: "sparkles")
                    .padding(.horizontal, 4)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(model.isBusy || !entry.canClean)

            if entry.applicationExists {
                Button("在 Finder 中显示", action: model.revealSelected)
                    .disabled(model.isBusy)
            }
        }
    }

    private func fileInspectionCard(_ inspection: FileInspection) -> some View {
        card {
            Label("文件的打开方式", systemImage: "doc.text.magnifyingglass")
                .font(.headline)
            Text(inspection.fileName)
                .font(.callout.weight(.medium))
                .textSelection(.enabled)
            metadata("默认应用", value: inspection.defaultApplication.isEmpty ? "系统未提供默认应用" : inspection.defaultApplication)
            Divider()
            Text("系统候选")
                .font(.caption)
                .foregroundStyle(.secondary)
            if inspection.candidates.isEmpty {
                Text("系统没有返回可用候选。")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            } else {
                ForEach(Array(inspection.candidates.enumerated()), id: \.offset) { _, candidate in
                    HStack(alignment: .top, spacing: 8) {
                        Image(systemName: "app")
                            .foregroundStyle(SuohTheme.accent)
                        Text(candidate)
                            .font(.callout)
                            .textSelection(.enabled)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
        }
    }

    private var statusBar: some View {
        HStack(spacing: 9) {
            if model.isBusy {
                ProgressView().controlSize(.small)
            } else {
                Circle().fill(SuohTheme.accent.opacity(0.65)).frame(width: 6, height: 6)
            }
            Text(model.statusMessage)
                .lineLimit(1)
                .truncationMode(.middle)
            Spacer(minLength: 12)
            Text("显示 \(model.visibleRecords.count) / \(model.records.count) 项")
                .monospacedDigit()
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .padding(.horizontal, 18)
        .padding(.vertical, 10)
        .background(SuohTheme.canvas)
    }

    private func card<Content: View>(@ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 14, content: content)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(20)
            .background(SuohTheme.canvas, in: RoundedRectangle(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(SuohTheme.accent.opacity(0.08)))
    }

    private func metadata(_ title: String, value: String, monospaced: Bool = false) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .font(.caption)
                .foregroundStyle(.secondary)
            Text(value)
                .font(monospaced ? .system(size: 12, design: .monospaced) : .system(size: 13))
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func walkthrough(_ number: String, title: String, text: String) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Text(number)
                .font(.system(size: 12, weight: .semibold, design: .rounded))
                .foregroundStyle(SuohTheme.accent)
                .frame(width: 25, height: 25)
                .background(SuohTheme.rose, in: Circle())
            VStack(alignment: .leading, spacing: 5) {
                Text(title).font(.callout.weight(.semibold))
                Text(text)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private func statusColor(_ status: EntryStatus) -> Color {
        switch status {
        case .missing: return SuohTheme.accent
        case .installed: return Color(red: 0.18, green: 0.43, blue: 0.30)
        case .unavailable, .unknown: return Color(red: 0.56, green: 0.38, blue: 0.10)
        case .protected: return .secondary
        }
    }
}
