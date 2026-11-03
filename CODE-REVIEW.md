# 本轮代码审查结论

> 后续补做COM1–COM2原生串口，发现并修复读取超时与打开宽限问题；最新108项结果见 [SERIAL-VALIDATION.md](SERIAL-VALIDATION.md)，下文保留本轮96项审查历史。

日期：2026-11-03。基线业务提交431e787；先重跑81项测试，再结合覆盖报告审查主库36个C#文件、测试、构建与脚本。历史记录REVIEW.md与FIX-VALIDATION.md分别保留此前轮次，当前结论以本文和TEST-COVERAGE.md为准。

## 确认并修复的问题

| 问题 | 原因/触发 | 修复 | 回归测试 |
| --- | --- | --- | --- |
| P1：重连后旧请求仍可能无限等待 | 旧用户接收回调尚未返回，新连接先被发布；旧循环因版本不同跳过finally里的取消，旧awaiter又没有被新连接清掉 | 新连接在状态锁内发布前，先结束内置调度器的旧注册；保留版本保护，避免旧收尾清理新请求 | ConnectionGenerationTests：阻塞旧回调、断连、接受新连接，旧请求返回null；释放旧回调，新请求仍收到正确响应 |
| P2：UDP外部回调仍在创建器锁内 | ChannelCreated与_socket状态更新位于同一个lock；回调跨线程协调Dispose可能互相等待 | 构造/发布状态在锁内，外部回调在锁外；发布回调失败则关闭通道为ApplicationError，释放socket | UdpCallbackRunsOutsideCreatorLockAndFailureClosesItsChannel |
| P2：Out读取批次误判为超长单帧 | 共享发送辅助方法把低层流的一次ReadResult套用MaxPackageLength；一个批次可包含多个合法帧 | 公共发送继续单帧限制，低层Out按字节流批次发送；不取消UDP的固有数据报上限 | LegacyOutputBatchCanContainSeveralValidFrames |
| P2：组播错误延迟到Socket层 | Multicast=true配普通单播地址，或IPv6组播，直到分配/绑定socket后才失败 | 资源分配前校验IPv4组播范围和不支持的IPv6组播，保留清晰异常 | InvalidMulticastIsRejectedBeforeAllocatingASocket |
| 防御性校验：低层创建器未立即拒绝null filter | TCP可能先连接然后反复失败，串口可能先尝试打开端口，错误出口滞后 | 三种创建器在绑定/循环之前ThrowIfNull | CreatorsRejectNullFiltersBeforeBindingOrStartingRetryLoops |

另修正两处先前脚本编辑造成的相邻声明排版，不改变行为。

## 补测与验证

新增15项测试，总计96项；已验证的内容与覆盖率变化见TEST-COVERAGE.md。关键补测包括NextFilter传递Context并Reset、无进展循环、不完整EOF、编码失败不泄漏半帧前缀、条件移除旧awaiter、串口首次打开失败后恢复以及发送空UDP数据报。

覆盖测量：主库行覆盖率88.04%→91.45%，分支覆盖率71.73%→76.66%。包含真实串口实现的未执行行，不排除它们美化指标。

## 保留的限制与后续工作

- 同步业务回调不能被强制终止；内置调度器已处理连接边界，自定义调度器仍需实现自身的连接生命周期与取消策略。
- 无请求编号的协议无法彻底识别迟到响应；断线恢复不代表可以安全重发命令。
- 为兼容仍保留公开IsClosed setter、旧拼写和部分低层扩展点。它们不是推荐的新业务入口，未来主版本可集中迁移；本轮不顺手破坏API。
- 真实串口驱动、组播、多平台、x86运行与长时压测尚未执行，不能从96项测试推导“所有现场问题均已解决”。
- 日志接收器、自定义filter/encoder/scheduler属于外部扩展代码，业务应避免长期阻塞或复用非线程安全状态。

本轮已确认的缺陷均有修复与对应测试；没有把尚未复现或需要设备的设计边界写成“已经验证通过”。

## 本轮最终验收

- Debug与Release各96项全部通过，0失败、0跳过；TRX位于本地 `TestResults/Review-Final/`。
- Release与Release/x86均以`-warnaserror`构建，0警告、0错误。
- x86构建没有改变AnyCPU DLL的SHA256；随后默认`dotnet test --no-build --no-restore`运行AnyCPU路径，96项通过。
- TCP回环示例编译并运行成功：`Sent: 01020304; received: 01020304`。
- 原9类加本轮3类，共12类隔离故障变异均触发实际测试失败，未把编译失败当作检出成功。本轮3类分别是连接代次、UDP锁内回调和Out批次误判。
- 新增测试工具后的直接/传递依赖检查未报告已知漏洞。
- `.gitignore`排除构建/发布目录、覆盖率原始数据、TRX/dump及本地讨论稿，保留当前项目指南与可审阅覆盖率摘要。
