import SwiftUI

@main
@MainActor
struct OpenWithCleanerMacApp: App {
    @StateObject private var model = CleanerViewModel()

    var body: some Scene {
        WindowGroup("Open With Cleaner Desktop Suoh Edition") {
            CleanerMainView(model: model)
                .frame(minWidth: 900, minHeight: 620)
                .tint(SuohTheme.accent)
                .task {
                    if CommandLine.arguments.contains("--smoke-test") {
                        await model.runSmokeTestAndExit()
                    } else {
                        model.scan()
                    }
                }
        }
        .defaultSize(width: 1060, height: 700)
        .commands {
            CommandGroup(after: .newItem) {
                Button("扫描应用记录", action: model.scan)
                    .keyboardShortcut("r", modifiers: .command)
                    .disabled(model.isBusy)
                Button("检查文件的打开方式…", action: model.chooseFile)
                    .keyboardShortcut("o", modifiers: .command)
                    .disabled(model.isBusy)
                Divider()
                Button("导出诊断报告…", action: model.exportReport)
                    .disabled(model.isBusy || model.records.isEmpty)
                Button("打开操作记录", action: model.openBackupFolder)
            }
        }
    }
}
