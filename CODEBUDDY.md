# CODEBUDDY.md This file provides guidance to CodeBuddy when working with code in this repository.

## 常用命令

| 命令 | 说明 |
| --- | --- |
| `dotnet build -c Release --nologo` | 日常编译验证。项目启用 `Nullable` + `ImplicitUsings`，编译器/分析器警告是唯一的静态检查手段；目标是 0 警告 0 错误（已验证）。优先用 Release，因为 Debug 输出常被运行中的程序占用。 |
| `dotnet build -c Debug --nologo --no-incremental` | 全量重编译 Debug。若报 MSB3026/MSB3027「文件被另一进程使用」，先关闭运行中的「串口助手」，或在 Git Bash 中执行 `taskkill //IM SerialAssistant.exe //F`。这是环境占用，不是代码错误。 |
| `dotnet run --project SerialAssistant.csproj` | 启动 WPF 窗口（`WinExe`）。实际收发数据需要 Windows 桌面会话与串口设备。 |
| `dotnet publish -p:PublishProfile=win-x64` | 按 `Properties/PublishProfiles/win-x64.pubxml` 生成自包含单文件 exe 到 `publish/win-x64/`。 |
| `dotnet format SerialAssistant.slnx --verify-no-changes --no-restore` | 格式检查。必须显式传入 `.slnx`：同目录同时存在 `.csproj` 和 `.slnx`，不指定工作区会抛 `FileNotFoundException`。仓库当前有 2 处既存 WHITESPACE 差异（`Services/SerialPortService.cs` 第 55–56 行），命令返回非零属既有状态，勿当作回归；`dotnet format SerialAssistant.slnx` 可应用修复。仓库无 `.editorconfig`。 |
| `dotnet test SerialAssistant.slnx -c Release --no-build` | 仓库没有测试项目（无 xUnit/NUnit/MSTest 依赖，`SerialAssistant.slnx` 只含主工程），因此没有「运行单个测试」的命令。 |
| `dotnet new console -o ../probe -f net10.0` + `dotnet add ../probe package System.IO.Ports --version 10.0.11` | 项目实际采用的验证手段：临时探针工程用 `<Compile Include=".../Model/*.cs">`、`<Compile Include=".../Services/*.cs">`、`Infrastructure/CrashLogger.cs` 链入源码，写断言后 `dotnet run --project ../probe`，验证完删除临时工程。历史用例见 `.workbuddy-ai/memory/2026-10-28.md` 与 `.codebuddy/plans/`。 |

环境要求：.NET 10 SDK（已验证 10.0.401）与 Windows；目标框架 `net10.0-windows` + `UseWPF`。依赖仅 `CommunityToolkit.Mvvm 8.4.2` 与 `System.IO.Ports 10.0.11`。

## 架构

### 总览与装配方式

单项目 WPF 桌面应用，手工 MVVM，无 DI 容器。`App.xaml` 通过 `StartupUri` 打开主窗口，`MainWindow.xaml` 里用 `<Window.DataContext><vm:MainViewModel/></Window.DataContext>` 直接在 XAML 中实例化 ViewModel；无参构造函数再 `new SerialPortService()`。也就是说**运行时没有集中装配点**，依赖关系是在这两处硬编码的。`MainViewModel` 是 `ObservableObject` 的 `partial class`，靠 CommunityToolkit.Mvvm 源生成器产生 `[ObservableProperty]` 属性和 `[RelayCommand]` 命令；改动这些字段时不要手写同名属性或命令。唯一的注入入口是 `internal MainViewModel(ISerialPortService, Func<string, string?>?)`，供测试替换串口实现与日志路径选择器。View 层不直接访问串口服务，Model 只有不可变 record 与枚举。

### 三级有界接收流水线

这是全项目最需要跨文件理解的部分：三个有界 `System.Threading.Channels` 队列构成接收/显示主干与日志旁支，任何一处改成阻塞式写入都会拖垮串口回调线程：

1. `SerialPortService` 在 `SerialPort.DataReceived` 事件（IO 线程）中按 `BytesToRead` 批量读取，包装成 `SerialReceiveMessage` 后 `TryWrite` 进容量 512 的接收队列。`FullMode.Wait` 配 `TryWrite` 的作用是：队列满时返回 false，可精确累加 `DroppedBytesCount`，既不阻塞也不无界堆积。事件在退订后仍可能在途，处理器用引用比对 `_serialPort` 挡掉「自己关端口引发的 IOException」。另有每秒轮询 `GetPortNames()` 的端口监控，设备拔出即触发故障关闭。
2. `MainViewModel.ConsumeMessagesAsync` 是该队列唯一消费者，也是整个应用唯一做解码的地方：文本模式用有状态 UTF-8 `Decoder` 处理跨包多字节字符，HEX 模式走 `FormatHex`；随后按需插入显示时间戳、写实时日志、判断暂停，最后 `TryWrite` 进容量 2048 的显示队列。消费循环捕获所有异常，记 `CrashLogger` 后延时 500ms 重试，绝不允许接收管线静默死亡。
3. `FlushDisplayAsync` 每 75ms 批量取出（单次上限 64KB），经 `Dispatcher.InvokeAsync(Background)` 回到 UI 线程调用 `AppendToDisplay`；后者维护 VM 侧的 `StringBuilder` 显示缓冲，上限 100 万字符，超限裁到 80 万并通过事件整体重设文本框。

ViewModel 不持有 TextBox，而是用 `DisplayAppendRequested` / `DisplayResetRequested` / `DisplayCleared` 三个事件驱动 `MainWindow.xaml.cs` 的 code-behind 做增量 `AppendText`、整体替换、清空水印显隐和「仅在已滚到底时自动滚动」。整体替换而非按字符数裁剪，是因为 WPF 的换行归一化会让缓冲与文本框长度漂移；`_displayGeneration` 计数用于丢弃清空前已在途的 flush 批次。字节统计不是服务推事件，而是 VM 侧 200ms `DispatcherTimer` 轮询服务计数器，值未变化就不发属性通知。

### 连接状态机与关闭链路

`SerialPortService` 用 `int _state` + `Volatile` / `Interlocked` / `CompareExchange` 维护 `SerialConnectionState` 五态（不用 `volatile` 枚举，因为条件更新需要 CAS）。`StateChanged` / `CommunicationError` 事件经 VM 的 `RunOnUi` 转成属性通知与命令 `CanExecute` 刷新，UI 因此完全由状态驱动。`_lifecycleGate` / `_writeGate` 两把信号量串行化开关与写入；`TryFinishClose` 用 CAS 保证迟到的后台回调不会覆盖新状态。

关闭有软/硬两级超时（1.5s / 6s）：软超时保持 `Disconnecting` 等后台真正关完，**绝不提前宣布 `Disconnected`**，否则用户立刻重开会报端口被占用；硬超时降级 `Faulted`。写入用同步 `SerialPort.Write`（受 `WriteTimeout=1000` 约束）而非 `BaseStream.WriteAsync`，外加 3s 硬超时，避免驱动挂死时发送按钮永久变灰。服务层所有 `await` 都带 `ConfigureAwait(false)`：无竞争时 `WaitAsync` 会同步完成，否则 `serial.Open()` 会继续跑在 UI 线程上造成假死。`OpenAsync` 中 `new SerialPort(...)` 必须留在 try 内，否则构造抛异常时状态会永久停在 `Connecting`。

关窗是一条必须跨三个文件理解的链路：`Window_Closing` 先 `e.Cancel = true` 截停，禁用窗口后异步执行 `MainViewModel.DisposeAsync()`（5s 硬上限），最后**必须**用 `Dispatcher.InvokeAsync(Close)` 把真正的关闭投递回消息循环——WPF 在 `Closing` 期间置 `_isClosing`，同步调用 `Close()` 会抛 `InvalidOperationException`。VM 的释放顺序固定为：停 timer → 取消生命周期 CTS → 等待接收/显示任务结束 → 停日志（≤3s）→ 退订事件 → 释放串口，以此保证日志完整落盘且串口句柄不泄漏。两把信号量故意不 `Dispose`，因为释放时可能仍有 fire-and-forget 任务在 `WaitAsync`。

### 实时日志子系统

`RealtimeLogService` 是**单次会话**写入器：每次开启由 VM `new` 一个新实例，停止后 `Volatile.Write(ref _logService, null)` 且不可复用，以此规避 Channel 复用、重启竞态和半关闭状态机。收发线程通过 `Volatile.Read` 取当前实例并调用非阻塞的 `TryEnqueue`（容量 8192，满则累加 `DroppedEntries`）。`StartAsync` 负责真实可写性校验、以 `FileMode.Append` / `FileShare.Read` 打开 UTF-8 无 BOM 文件并写会话头；`LogPathDialog.TryResolvePath` 只做格式与静态校验，避免 TOCTOU。

后台写入任务用 100ms `PeriodicTimer` 固定节拍：排空队列、检查空闲断行、按 200ms 或 64KB 阈值批量写出、检测队列完成且已排空后最终落盘。接收侧按设备换行聚合（跨包拼成一行、遇 `\n` 输出、空闲 300ms 断行、单行 4096 字符强制断行、行首取首个分片时间戳、`\r\n` 只裁一个 `\r`、保留空行）；处理 TX 记录前强制刷出未完成的 RX 行，防止发送记录插进接收行中间。写盘先 `FlushAsync` 保证可见，再按 2 秒或 1MB 执行 `FileStream.Flush(true)`。`StopAsync` 幂等（lock + 缓存 `_stopTask`），故障时通过 `Faulted` 事件让 VM 在 UI 线程自动停止并只提示一次。

业务语义上：「暂停显示」只影响界面，日志仍持续记录；只有「暂停且日志关闭」才走零解码快速丢弃。日志 payload 不含显示用时间戳前缀，避免与日志行自身的毫秒时间戳重复。TX 只在 `WriteAsync` 成功返回后记录，且 data 已包含可选的 CRC16-Modbus 追加字节。清空接收区不影响已写入文件。

### UI、异常与发布

视觉资源全部集中在 `App.xaml` 合并的 `style/CommonStyle.xaml`（哆啦A梦主题：矢量 `DrawingImage`、调色板、Button/CheckBox/ComboBox 的完整 `ControlTemplate`）。状态变化靠 `DataTrigger` 绑定 `ConnectionState` / `IsOpenPort` / `IsLogSaving`，没有值转换器。圆角输入框由外层 `Border` 提供而 `TextBox` 保持透明无边框，自定义其模板会破坏 `ScrollToEnd()` 与滚动行为；正文区域另需留意 WPF 父元素上的 `Effect` 会关闭子树 ClearType。

`App.xaml.cs` 注册三层全局异常钩子（Dispatcher 连续 3 次后 `Shutdown(1)`、AppDomain、`TaskScheduler.UnobservedTaskException`），统一写入 `Infrastructure/CrashLogger`（`%LOCALAPPDATA%\SerialAssistant\logs\crash_yyyyMMdd.log`，该方法自身绝不抛异常，以免在崩溃路径上递归崩溃）。

发布配置为自包含单文件 win-x64。WPF 既不支持 NativeAOT 也不支持 `PublishTrimmed`（会裁掉 XAML 反射所需信息），约 59MB 是体积下限；`IncludeNativeLibrariesForSelfExtract=true` 不可省，因为 `System.IO.Ports` 有 native 依赖。命令行传 `-p:SatelliteResourceLanguages=zh-Hans;en` 时 `;` 会被 MSBuild 当作属性分隔符，须写成 `%3B`（pubxml 内直接写 `;` 无碍）。

### 明确排除的范围

以下内容是有意未实现，不是待补的漏洞：定时/循环发送、GBK 等其他编码、参数持久化、原始字节日志、流控与 DTR/RTS、多串口、自动重连、DI 容器、单元测试工程。历史设计决策与踩过的坑记录在 `.workbuddy-ai/memory/2026-10-28.md`，日志功能的两版设计在 `.codebuddy/plans/` 下的两份计划文档中；改动接收、关闭或日志链路前应先读它们。
