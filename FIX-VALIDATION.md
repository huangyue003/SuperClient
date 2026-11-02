# 本轮修复与验证（2026-11-03）

> 本文保留上一轮81项测试阶段的历史记录；最新96项测试、覆盖率与后续修复见 [CODE-REVIEW.md](CODE-REVIEW.md) 和 [TEST-COVERAGE.md](TEST-COVERAGE.md)。

## 实施决策与行为变化

- B1：TCP/串口的公共 SendAsync 现在等待本次全部字节被底层接受；不保证对端收到或执行，不自动重发。原始字节直接发送；编码器/Action 使用有硬容量限制的池化 Pipe 分段缓冲，发送完成前保留段，不再 ToArray 全量复制。
- SendTimeout 是单次调用的准备、等锁、底层发送预算。同步用户编码器/Action无法被强行中断：计时包含其耗时，但超时只能在回调返回后被观察。排队超时尚未发送任何字节，不关闭其他正在发送的连接；发送中超时则关闭连接，避免残留半帧继续复用。
- Q1/Q2：只忽略生命周期已关闭时的预期取消。局部超时token到期且生命周期未取消，转换为 TimeoutException；保存首次通道故障后才取消其他操作。传输故障使用 ChannelTransportException，包含原因、已接受/待发送字节数和原异常。发送超时日志为Error，含Identity、阶段、原因、字节数。
- Q3：SendReceiveAsync的millisecondsTimeout继续仅控制响应等待，不含串行请求排队与发送。总时长不是一个固定上界：还包括排队，且SendTimeout=0不限制发送。不能声称“总上限恒等于SendTimeout+timeout”。
- Q4：每帧发送容量受MaxPackageLength（UDP还受65507）限制；GetMemory请求和Advance均先校验容量。暂不做额外的缓冲池/散聚写微优化。
- 原测试迁移：ZeroSendCountClosesInsteadOfSpinning 必须改为断言 ChannelTransportException。旧断言隐含“底层失败仍正常返回”，与B1约定冲突；仍保留SocketError关闭原因断言。后续容量分类的另外两项迁移见下节。
- 公共setter、旧拼写和扩展点保留；不无条件启用ReuseAddress；不默认开启串口重连。CI与广泛warnings-as-errors本轮不擅自开启。

以下为本轮最终实施与验收记录。

## 质量门禁逐项结论

| 门禁 | 结果与依据 |
| --- | --- |
| Q1 超时日志 | 实际发送超时记录Error，包含Identity、阶段、TimeOut、accepted/pending；RecordingLogger断言消息内容。排队超时记录Warning，不归咎于传输。 |
| Q2 取消原因 | 只有生命周期未取消、局部计时token已取消才归为发送超时；其他OCE视为原始传输故障或既有关闭。原子保存首次Failure，后续清理不能重写Reason。 |
| Q3 超时累加 | 响应等待预算保持不变，新增XML与README说明；不把SendTimeout+responseTimeout冒充总上限，因为前面还可能无限排队。 |
| Q4 拷贝与容量 | 直接字节路径不做暂存复制；Action/编码器用LimitedPipeWriter限制GetMemory/Advance，直接发送ReadOnlySequence并保留段；不做整帧ToArray。代价见下表。 |
| Q5 关闭清理 | 先非阻塞尝试锁，再进行可取消等待；关闭时跳过忙锁，由当前发送所有者做安全收尾。清理捕获异常不覆盖首个故障。锁被外部代码永远占用也不阻塞通道处理循环结束。 |

## 旧测试迁移（没有静默放宽断言）

原42项测试保留，3项断言因明确修正的契约而更新：

1. ZeroSendCountClosesInsteadOfSpinning：从发送正常返回改为精确断言ChannelTransportException，仍断言SocketError。
2. QueueOverflowClosesChannel：ProtocolError改为InternalError，这是B5要求的分类修正。
3. ObjectPipeDrainsAndCompletesPendingConsumer：容量异常从通用InvalidOperationException改为精确的内部ChannelQueueOverflowException。

TcpServerTest去掉占用再释放端口的窗口，改为绑定端口0并查询LocalEndPoint，未削弱收发及下一连接断言。旧测试中已有的部分轮询未作无关全面重写；新增关键失败测试使用信号控制。实际超时测试不可避免使用配置计时器，但不靠任意Sleep制造传输时序。

## 本轮条目覆盖与保留判断

| 条目 | 处理 |
| --- | --- |
| B1/B3/B28 | 公共发送统一等待底层完成、精确异常、UDP链接生命周期、逐请求预算和首故障传播；不新增保留旧假成功语义的SendQueuedAsync。 |
| B2/B30 | Detach明确拒绝且不改状态；关闭清理锁可取消，阻塞回调不阻止关闭；B1、B30已分别提交。 |
| B4/B5 | URI/端点错误归ArgumentException并含上下文；队列满归InternalError并记录容量事实。 |
| B6/B11/B20 | 启动及接收同步前缀移出状态锁；断连事件携带原通道快照与Reason；观察者异常记录日志且不妨碍其他观察者。 |
| B7/B8/B16/B21/B23 | 连接超时、退避抖动及日志汇总；端口0可查询；增加ChannelOptions重载和URI参数；去掉logger!；NoDelay/KeepAlive可配置，默认维持原行为。 |
| B9 | 串口恢复显式启用、默认关闭，带退避与停止条件、不重发命令；通过可控假端口验证，真实驱动仍需硬件。 |
| B10/B27 | 公共串行能力接口，支持独立实现；条件删除使用TryRemove(KeyValuePair)，防清理掉新注册。原ICollection.Remove是ConcurrentDictionary的哈希条件删除实现，不是线性扫描，因此不宣称获得O(n)到O(1)收益。 |
| B12 | 延后直接Dispose长期同步原语。DisposeChannelAsync后还允许Reset，且可能有用户回调尚未返回；仓促Dispose会引入与等待者竞争。每次调用创建的超时源及token注册均已using释放。 |
| B13/B14/B15 | 不做未经负载证明的散聚写/共享编码缓冲重构。当前逐段循环已保留部分发送与取消契约；原始字节近零分配，Action无需ToArray。编码路径仍有每次暂存成本，见实测。 |
| B17/B18 | 不删公共成员，不收窄setter；关闭是否结束改由内部标志判断，外部设置IsClosed=true不再跳过实际资源关闭，并有回归测试。泛型参数“加别名”不适合作为API迁移方案，保留现有拼写。 |
| B19/B26 | 无参串口配置对象提供9600/8/One合理设置，端口名仍须填写。保留合法数值枚举以兼容StopBits=1等现有URI，未定义值拒绝，报错列允许名称。 |
| B22 | bin/obj隔离，slnx显式x86映射，发布路径同步验证；公共Nullable/ImplicitUsings上提，editorconfig落地。CI工作流与广泛新分析规则不在本轮默认启用，验收命令单独使用-warnaserror。 |
| B24/B25 | 保留默认容量避免偷改吞吐行为；文档给负载估算及更小配置示例，明确空闲读取超时会断连。 |
| B29 | 不设置ReuseAddress，也不凭假设设置ExclusiveAddressUse/Linger。未复现需更改默认绑定策略的问题，留作目标平台网络策略验证。 |
| 其他观测 | 通道新增LastSendTime/LastReceiveTime，调度器新增UnmatchedPackages，计数不等同丢包。 |

## 失败回退验证

`scripts/Test-QualityMutations.ps1`在临时Git快照中模拟恢复关键错误语义，要求实际生成TRX且至少一个测试失败；编译失败不算验证通过。真实工作区源码不被修改。

| 错误语义 | 对应失败测试数 |
| --- | --- |
| B1 提前按入队成功返回 | 6 |
| B30 关闭清理不可取消等锁 | 1 |
| B2 Detach静默成功 | 2 |
| B5 队列满误分类ProtocolError | 1 |
| B4 绑定端点直接抛FormatException | 2 |
| B7 固定200ms重试 | 1 |
| B6 在状态锁中Start | 1 |
| B3 已关闭错误映射为任务取消 | 1 |
| B28 不链接UDP发送生命周期token | 1 |

这是针对关键行为的变异测试，不声称逐个git revert任意提交都能独立编译；配置能力与发送状态存在合理的提交依赖。

## 小包性能实测

Windows x64、.NET10、Release；16字节，传输立即接受的假通道；每种入口每轮预热2000次、测量20000次，共3轮。对比c27f8c0与78ee988；取三轮P99的中位数。分配量为进程累计分配的每调用估算，包含后台处理。

| 入口 | 修复前P99 | 修复后P99 | 每调用分配前→后 |
| --- | --- | --- | --- |
| 原始字节 | 2.8µs | 0.5µs | 约100B → 约0B |
| Action | 0.7µs | 4.1µs | 约1B → 约648B |
| 编码器 | 1.4µs | 1.5µs | 约424B → 约744B |

Action入口确有相对延迟与分配退化，不能宣称完全无成本；新增私有池化Pipe用于隔离回调、限制帧容量并保留分段内存直到实际发送。这里报告绝对成本，不掩盖相对倍数。对约300次/秒调用，648B意味着约194KB/秒分配，但真实负载仍应测量。

假传输测量的是库内开销，不包含网络、驱动或设备延迟；旧版等待入队，新版等待底层接受，契约本身也不同，因此不能据此宣称真实网络更快。可用`scripts/Measure-SendLatency.ps1`重跑。

## 外部边界

真实串口/驱动取消行为、组播网络、Linux/macOS、x86运行时和长期现场压测仍未验证。自动连接恢复不等于安全重试命令；无请求编号的迟到响应仍需协议解决。阻塞的同步用户回调不能强制终止，关闭可以完成但回调任务必须由调用方解除。

## 最终验收结果

- Debug：81/81通过，0失败、0跳过；记录 `TestResults/Final/debug.trx`。
- Release：81/81通过，0失败、0跳过；记录 `TestResults/Final/release.trx`。
- Release与Release/x86使用`-warnaserror`构建均成功，0警告、0错误。
- x86构建前后AnyCPU文件SHA256一致；随后默认`dotnet test --no-build --no-restore`仍运行`bin/AnyCPU/Release/net10.0/`，81/81通过。
- CLR标志：AnyCPU为ILOnly，x86为ILOnly+Requires32Bit。两者的PE Machine都可能显示I386，因此不只用Machine判断架构。
- AnyCPU发布成功，目录 `IFactory.Interaction.SuperClient/bin/AnyCPU/Release/net10.0/publish/`。
- x86发布成功，目录 `IFactory.Interaction.SuperClient/bin/x86/Release/net10.0/publish/`。本次没有执行x86测试运行时。
- 9类隔离变异全部被测试检出；汇总 `TestResults/quality-mutations.csv`。性能原始样本 `TestResults/send-latency.csv`。
- 未新增第三方依赖；原有SuperSocket.ProtoBase解包契约保留。
- Workbuddy的DISCUSSION/HANDOFF/QUALITY-GATE及上一轮CODEX-RESPONSE保持原样，未混入修复提交；本轮改动分项提交在本地，未推送GitHub。
