---
name: 日志按设备换行聚合
overview: 修复实时日志按串口分包逐行写入导致 JSON/设备日志被切碎的问题：文本模式改为按设备换行聚合，无换行碎片拼成完整行，遇 \n 或空闲约 300ms、超长时落一条日志；保留 ANSI 原样，停止时刷出未完成行。
todos:
  - id: aggregate-rx-lines
    content: 在 RealtimeLogService 实现 RX 行聚合、空闲与超长断行、TX 前刷行及多行 TX 输出
    status: completed
  - id: restructure-pump
    content: 将 PumpAsync 改为 PeriodicTimer 节拍循环，增加输出节流与停止时强制刷行落盘
    status: completed
    dependencies:
      - aggregate-rx-lines
  - id: verify-aggregation
    content: 构建并用临时工程验证分片聚合、CRLF、空闲断行、TX 顺序与停止落盘
    status: completed
    dependencies:
      - restructure-pump
---

## 产品概述

修复实时日志把一次串口读取包写成一行、导致完整 JSON 与设备日志被切碎的问题。文本模式下改为按设备换行聚合，让日志行与设备实际输出行一致，提升可读性与可解析性。

## 核心功能

- 无换行的接收碎片自动拼回完整一行，遇到 `\n` 才写入一条日志。
- 空闲约 300ms 没有新数据时，把未完成的行强制写入；单行超过约 4KB 时强制断行，避免无换行数据无限增长。
- 时间戳取该行第一个分片到达的时间，保留毫秒精度。
- 发送数据插入时，先写出尚未完成的接收行，保证 RX/TX 顺序不串行。
- 文本模式与 HEX 模式均适用；HEX 每条读取包本身带换行，行为不变。
- ANSI 颜色控制字符原样保留，不剥离、不转义。
- 停止日志或关闭程序时，刷出未完成行并完整落盘。
- 接收区显示、导出、清空、暂停显示等现有行为保持不变。

## 技术栈

- 沿用 WPF `net10.0-windows`、`System.Threading.Channels`、`FileStream` + `StreamWriter`，不新增依赖。
- 仅修改 `Services/RealtimeLogService.cs`，ViewModel 的入队接口 `TryEnqueue` 保持不变。

## 实现思路

把“行聚合”放进 `RealtimeLogService` 的后台写入任务，而不是 ViewModel：

1. **入队不变**：RX/TX 仍以一次串口读取包/一次发送为一条 `LogEntry` 进入有界 Channel，`DroppedEntries`、`Faulted`、`IsEnabled`、`StopAsync` 幂等语义保持不变。
2. **后台任务改为固定节拍**：用 `PeriodicTimer`（约 100ms）替代 `WaitToReadAsync` 阻塞循环，每个节拍：

- 从 Channel 批量取出条目，交给聚合器；
- 检查未完成接收行是否已空闲超过 300ms；
- 按 200ms 或累计 64KB 的阈值写出输出缓冲；
- 检测 Channel 已完成且已排空后，执行最终刷行、写出与 `Flush(true)` 后退出。

3. **接收行缓冲**：维护 `StringBuilder`、行首时间戳、行首到达时间。逐字符扫描，`\n` 触发行输出；输出前去掉一个尾部 `\r`，正确处理 `\r\n`；空行保留为一条带前缀的空记录；超过 4096 字符立即断行。
4. **发送处理**：处理 TX 前先强制输出未完成的 RX 行；TX 内容按 `\n` 拆成多行，每行带 TX 前缀，末尾换行不额外产生空记录。
5. **停止与故障**：`StopCoreAsync` 完成 Channel 后等待后台任务排空并刷行；写入故障仍走现有 `HandleFault`，不改变 UI 的自动停止与单次提示逻辑。
6. **时间戳**：RX 行使用该行第一个分片的 `DateTimeOffset`，后续分片不更新时间；TX 行使用发送时间。

## 关键决策与权衡

- **聚合放在服务层**：接收线程只负责解码与入队，避免在串口回调线程做字符串拆分；服务单消费者，行缓冲无需加锁，停止时由同一任务刷出，天然无竞态。
- **固定节拍而非等待唤醒**：空闲断行必须有定时器；`PeriodicTimer` 每 100ms 复用同一个计时对象，避免高频数据下反复创建 `Task.Delay`。代价是最多约 100ms 的额外写入延迟，与现有 200ms 批量写一致。
- **300ms 空闲断行**：兼顾“多包组成一行”和“无换行设备及时落盘”。超过阈值仍无换行则视为一行结束，符合串口流式数据特征。
- **4KB 单行上限**：防止无换行二进制流把内存与单行日志无限撑大；超长行会分成多条带时间戳的记录。
- **TX 前强制刷 RX 行**：宁可把一条正在接收的长行提前断开，也不让 TX 记录插进 RX 行中间，保证日志顺序与方向标识准确。
- **不剥离 ANSI**：按用户选择完整保留设备输出，避免改变原始字节语义。

## 性能与可靠性

- 聚合为 O(n) 单遍字符扫描；按 100ms 批量处理，后台线程执行，UI 与串口接收线程零阻塞。
- 输出缓冲按 200ms/64KB 写出，`Flush(true)` 仍按 2 秒或约 1MB 执行，磁盘写入频率不增加。
- 行缓冲上限 4KB、Channel 上限 8192、输出缓冲按批写出，内存有界；队列满仍计数丢弃，不阻塞。
- 停止路径最多等待一个 100ms 节拍加一次落盘，仍在窗口关闭 5 秒硬上限内。

## 实现注意

- 行缓冲与输出缓冲仅由后台任务访问，不要放入 `TryEnqueue` 或 UI 线程。
- 处理 `\r\n` 时只去掉一个尾部 `\r`，避免产生 `\r\r\n` 或双空行；单独 `\r` 不作为行尾，仅在输出时裁掉尾部。
- Channel 完成检测必须同时满足 `Completion.IsCompleted` 与 `TryRead` 为空，避免提前退出丢失尾部数据。
- 保留现有会话头、追加写入、文件共享与故障上报逻辑，不修改导出与显示缓冲。
- 日志中的 ANSI 控制字符按原样写入，不做可见化转义。

## 目录结构

```
SerialAssistant/
└── Services/
    └── RealtimeLogService.cs   # [MODIFY] 新增 RX 行聚合缓冲与时间戳字段；
                                #          将 PumpAsync 改为 PeriodicTimer 节拍循环；
                                #          新增 AppendRxPayload/FlushRxLine/AppendTxPayload/
                                #          WriteOutputAsync 等方法，替换 AppendEntry；
                                #          停止时强制刷出未完成行并 Flush(true)。
```

## 验证方案

使用临时控制台工程引用主项目，模拟分片入队并读取文件断言，验证后删除临时工程：

- 无换行 JSON 分 10 个包入队，空闲后落为一条完整 RX 行。
- 带 `\n` 的 ESP 日志每条一行，ANSI 控制字符保留。
- `\r\n` 与 `\n` 混用不产生多余空行；中间空行保留。
- 空闲 300ms 自动落行；4KB 超长行被拆分。
- RX 未完成时插入 TX，输出顺序为 RX 行、TX 行，不串行。
- 停止/关窗时未完成行完整落盘；追加、队列丢弃、故障停止行为不回归。
- `dotnet build` 零警告零错误。