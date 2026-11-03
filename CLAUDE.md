# 录屏 · 协作约定（Mac / Windows 两台电脑上的 Claude 都要遵守）

一个仓库两套代码：Mac 版 = 根目录 `*.swift`（SwiftUI），Windows 版 = `windows/*.cs`（C# WinForms，.NET Framework 4.8）。
用户会在两台电脑上分别让 Claude 改，所以：

1. **动手前先 `git pull`，改完马上 commit + push**，别在本地攒着，免得两边冲突。提交作者用 noreply 邮箱。
2. **功能要两边对齐**：改了一边的用户可见功能，就去看另一边有没有对应的。没有的话在 README 那一节里写明「Mac 版暂无 / Windows 版暂无」，别默默只改一边。
3. **只有一边有、而且是故意的**，写明原因。例：截图 + 截图缩略图只在 Windows 版（Mac 用系统 ⌘⇧4，系统缩略图已能拖、能往右划）。
4. **`project.json`（Studio 素材工程文件）两边通用**：加字段时两边名字、含义、默认值一致；不认识的字段原样保留，不许丢。
5. **Windows 版不能在 Mac 上编译**：在 Mac 上改 `windows/` 只能推上去让 GitHub Actions（`.github/workflows/windows.yml`）编译并跑自测，看结果再说「改好了」。Windows 电脑上可以本地编译、直接试。
6. 新功能尽量配自测（Windows：`windows/SelfTest*.cs`、`StudioTests*.cs`；Mac：`DTest.swift`），README 中英两段都要更新。
7. Windows 发给用户只给单个 exe，依赖嵌进去，首次运行解到 `%LOCALAPPDATA%\录屏`。
