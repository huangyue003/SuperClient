# 测试覆盖率与补测评估


> 最新复审（2026-11-03）：x64 Debug/Release与x86 Release均120/120通过，包含8项COM1–COM2原生测试；Release覆盖906/968行（93.59%）、506/645分支（78.44%）。以下保留此前阶段记录；最新行为和重跑命令见 [RECONNECT-REVIEW.md](RECONNECT-REVIEW.md)。

> 后续已使用本机COM1–COM2执行原生驱动测试：启用时108项通过，行覆盖93.31%、分支77.58%。以下96项数据保留为无串口阶段基线，最新串口范围与结果见 [SERIAL-VALIDATION.md](SERIAL-VALIDATION.md)。

测量日期：2026-11-03。范围为主库IFactory.Interaction.SuperClient，Release/Windows x64/.NET10，VSTest + coverlet.collector 10.1.0。没有排除串口类、接口默认实现或自动属性来提高覆盖率。入口配置为coverage.runsettings，原始报告保存在本地TestResults目录。

## 1. 实测结果

| 阶段 | 测试数量 | 行覆盖 | 分支覆盖 |
| --- | --- | --- | --- |
| 本轮开始，提交431e787的业务代码 | 81，全部通过 | 810/920，88.04% | 434/605，71.73% |
| 复审修复与补测后 | 96，全部通过 | 856/936，91.45% | 473/617，76.66% |

新增15项用例，不仅检查正常收发，也验证协议切换、失败隔离和连接代次。分母随缺陷修复新增分支而变化，因此同时列出命中数/总数。测试数量不是覆盖率，覆盖率也不是不存在bug的证明。两次相同配置测量的分支命中为473或474（76.66%–76.82%）；差异定位到TCP重连finally中ReferenceEquals的关闭竞争路径，属于调度差异。上表采用脚本最后一次输出，不挑选最高数字。

## 2. 已覆盖的关键行为

| 范围 | 主要用例 |
| --- | --- |
| 真实TCP/UDP loopback | TCP大帧收发与重连、服务端下一连接、端口0、UDP数据报边界/60KB接收/空数据报 |
| 发送与取消 | 小包等待真实接受、大包超时、部分发送不重复、故障原因和字节数、多调用独立完成、排队超时、UDP生命周期取消 |
| 关闭与状态 | 忙锁和阻塞同步回调、原始异常保留、Detach明确拒绝、最后响应先于EOF、在途请求跨Reset |
| 连接代次 | 旧用户回调仍阻塞时，新连接清理旧请求；释放旧回调后新请求仍能匹配 |
| 解帧状态 | 固定长度/固定头/分隔符、NextFilter上下文与Reset、跨读取分片、无进展切换循环、不完整EOF、累计帧长 |
| 调度器 | 无键串行、独立实现声明串行、有键端到端乱序、重复键拒绝、旧awaiter不能误删新注册、断连批量完成、未匹配计数 |
| 配置/缓冲 | URI错误、合法/非法串口配置、连接超时/退避/日志限频、受限PipeWriter、编码抛错不泄漏半帧前缀 |
| 高级入口 | Out合并读取批次包含多个合法帧，不误判单帧长度；UDP回调位于锁外，失败释放通道 |

## 3. 重点文件的行覆盖率

| 文件 | 行覆盖 | 解释 |
| --- | --- | --- |
| PipeChannel.cs | 177/188，94.15% | 包流、读写、关闭、filter切换已覆盖；仍有部分异常收尾和兼容辅助路径 |
| PipeChannel.Send.cs | 74/79，93.67% | 等待接受、分段失败与取消覆盖较好，个别竞争组合仍非穷举 |
| SuperClient.cs | 137/146，93.84% | 初始化、代次、Reset、通知、请求响应；自定义行为仍需应用场景验证 |
| TcpChannelCreator.cs | 80/82，97.56% | 可控连接超时与退避、真实accept/connect；长期网络异常不等于已验证 |
| UdpChannelCreator.cs | 47/52，90.38% | 校验、锁外发布、失败关闭；真实组播加入分支未测试 |
| UdpPipeChannel.cs | 15/15，100% | 不代表所有网络/平台分支均覆盖，整库分支率仍约为76.7% |
| KeyPackageScheduler.cs | 17/17，100% | 匹配/重复/取消/条件删除已覆盖 |
| LimitedPipeWriter.cs | 17/17，100% | 容量检查及Flush/Complete转发已覆盖 |
| SerialIOChannelCreator.cs | 56/70，80.00% | 连接恢复策略有假端口测试；实际Open/驱动资源路径未覆盖 |
| SerialIOPipeChannel.cs | 3/8，37.50% | 没有真实串口对，不把假I/O说成硬件验证 |

默认接口回退、旧扩展辅助方法等少量代码覆盖较低。本轮不为把数字抬到100%而添加仅复述实现的测试，优先测试影响协议、资源与在途请求的行为。

## 4. 是否还需要补测试

需要，但下一批的主要价值来自环境和负载，而非继续堆普通单元用例：

| 优先级 | 建议 | 所需条件 |
| --- | --- | --- |
| 高 | 虚拟对读写、读取取消、关闭重开和恢复已验证；物理拔插、其他驱动与硬件流控仍待验证 | 目标物理设备 |
| 高 | 持续长时间并发发送/重置/断线，观察任务和内存是否稳定、队列满时诊断是否准确 | 压测环境与明确设备协议 |
| 中 | 组播加入、跨网卡、路由与防火墙、IPv6单播 | 可控网络环境 |
| 中 | Linux/macOS与x86运行时回归 | 对应主机/运行时；当前x86仅构建/发布 |
| 中 | 延迟、吞吐和GC在真实报文大小/频率下的分位数 | 生产代表性负载；假传输基准不能替代 |

目前普通单元和loopback测试足以作为日常回归门禁，不能替代现场设备验收。尚不建议用统一90%分支硬门槛逼迫排除硬件代码或制造低价值测试；应结合这里的风险矩阵评审新增未覆盖分支。

## 5. 如何重跑与查看

```powershell
./scripts/Measure-Coverage.ps1 -Configuration Release
```

脚本执行测试、确认覆盖率报告真实存在，并生成唯一目录：

```text
TestResults/Coverage/<run-id>/
├─ summary.json      总体行/分支覆盖与报告绝对路径
├─ summary.csv       逐文件行/分支覆盖
└─ <collector-id>/coverage.cobertura.xml、coverage.json
```

也可以直接执行：

```powershell
dotnet test IFactory.Interaction.SuperClient.slnx -c Release --settings coverage.runsettings --collect "XPlat Code Coverage" --results-directory TestResults/Coverage
```

Coverlet是PrivateAssets=all的测试工具，不进入主库运行依赖。原始XML/JSON、TRX、dump和本机绝对路径报告不提交；本Markdown保存可审阅的测量口径、结果和边界。
