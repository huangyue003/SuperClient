# 最终建议落实与重连复审

日期：2026-11-03。参考Workbuddy的REVIEW-FINAL.md，核对源码后落实以下修改。

| 建议 | 判断与修复 | 验证 |
| --- | --- | --- |
| TCP过早清除失败状态 | 有效：filter.Reset、通道构造和ChannelCreated成功后才记录恢复、清零失败次数；启动失败继续累计退避 | 真实TCP回环分别注入Reset异常与回调异常，验证200/400ms退避和日志 |
| 串口InternalError恢复 | 有条件采纳：新增SerialReconnectOnInternalError，默认false，且必须开启SerialReconnect；本地、应用、协议关闭仍停止 | 默认终止、显式恢复、100/200/400/400ms连续故障退避、其他可恢复原因清零 |
| 重连复用打开宽限 | 有效：一次性打开和每次重连都使用SerialOpenRetryTimeout；仅重试UnauthorizedAccessException，预算耗尽进入普通连接退避 | 短暂拒绝、持续拒绝、宽限期间Dispose取消 |
| x86运行与标记 | 之前的Machine=014C不能独立证明32位要求；当前文档中的CorFlags说明正确。本机已有x86运行时，现已执行x86测试 | x86库及测试程序集Requires32Bit；实际x86测试120项通过 |
| LastCloseReason并发读取 | 使用int哨兵与Volatile.Read/Write，避免跨线程读取可空枚举的复合状态 | 完整生命周期回归；逐连接诊断仍应使用Disconnected事件快照 |

InternalError计数独立于打开失败计数：成功打开不清除连续内部关闭次数，遇到其他可恢复关闭原因才清零；延时受ReconnectMaxDelay限制。恢复仅重建连接，丢弃旧接收队列，绝不重放业务命令。应先改善消费速度、容量或协议，避免长期反复溢出。

新增原生测试OptInQueueOverflowRecoveryReopensPortAndDropsTheOldFrameBacklog：COM2向COM1连续写入三帧触发容量1的真实队列溢出，检查InternalError、自动重开、新帧独立消费及双向收发，验证无旧命令重放。

## 验证结果

- Release警告视为错误构建通过。
- x64 Release、x64 Debug、x86 Release分别120项通过，0失败、0跳过，均包含8项原生串口测试。
- Release覆盖率：906/968行（93.59%），506/645分支（78.44%）；保留串口代码在统计范围内。
- x86构建及运行后，AnyCPU Release库的SHA256保持不变。
- 关闭串口环境变量时有112项普通测试，8项原生测试按显式配置跳过；不自动探测串口。

```powershell
$env:MSBuildEnableWorkloadResolver = 'false'
$env:SUPERCLIENT_SERIAL_PORTS = 'COM1,COM2'
dotnet build IFactory.Interaction.SuperClient.slnx -c Release -warnaserror
dotnet test IFactory.Interaction.SuperClient.slnx -c Release
dotnet test IFactory.Interaction.SuperClient.slnx -c Debug
dotnet test IFactory.Interaction.SuperClient.slnx -c Release -p:Platform=x86 -- RunConfiguration.TargetPlatform=x86
./scripts/Measure-Coverage.ps1 -Configuration Release
Remove-Item Env:SUPERCLIENT_SERIAL_PORTS
```

最终复审重点核对关闭原因分类、退避计数重置时机、打开宽限与生命周期取消、TCP启动异常后的资源释放、URI解析与选项复制。未发现需要继续修改的确定性缺陷；测试不证明不存在其他问题。真实UART/USB拔插、其他操作系统、组播环境和长时负载仍按此前约定暂缓。本次没有新增CI工作流，x86结果来自本机真实测试进程。
