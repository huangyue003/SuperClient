# COM1–COM2 原生虚拟串口验证

日期：2026-11-03。Windows x64、.NET10，实际打开本机COM1和COM2，115200波特率、8数据位、无校验、1停止位。这里经过System.IO.Ports和本机虚拟串口驱动，区别于之前仅使用内存假传输的策略测试；仍不等同真实UART/USB设备验收。

## 本次结果

- 初始双向连通性探测成功。
- 新增7项原生集成用例及5项不依赖端口的回归测试。
- 显式开启串口配置后，Debug和Release均108项全部通过，0失败、0跳过。
- 未开启时，普通测试应101项通过、7项明确跳过；不会自动扫描并占用开发者的串口。
- 原生测试集合禁止并行；每项使用后关闭所有端口，测试失败也等待关闭完成。
- 主库Release行覆盖893/957（93.31%），分支488/629（77.58%）；SerialIOPipeChannel行覆盖100%，SerialIOChannelCreator行覆盖96.25%。覆盖率不证明所有驱动/硬件行为正确。

## 七项实际驱动测试

| 用例 | 验证内容 |
| --- | --- |
| NativeRequestResponsePreservesAResponseSplitAcrossDriverReads | 发请求，确认读到响应前半帧后才发送后半帧；ReadAsDemand下仍能组帧 |
| TwoLibraryClientsCommunicateInBothDirections | 两端均使用SimplifiedClient，双向发送并核对逐字节内容 |
| NativeReadDecodesMultipleFramesWithoutDroppingTheTail | 一次发送两个完整帧，验证不丢粘包尾部 |
| ClosingAnIdleNativeReadReleasesThePortAndAllowsReset | 正在等待数据时关闭，Closed只通知一次；原生重新打开端口并再次Reset、收发 |
| TenNativeResetCyclesContinueToExchangeExactFrames | 连续10次Reset，每轮请求响应一致，最后仍可收发；这不是长时压测 |
| NativeReadTimeoutClosesTheChannelWithoutPeerTraffic | 对端保持打开但不发送，ReceiveTimeout实际关闭通道并报告TimeOut |
| OptInRecoveryReopensTheNativePortAfterAnExplicitFaultClose | 显式以SocketError关闭旧通道后，开启的恢复策略真实重开端口，不重放旧命令；不是模拟物理拔插 |

## 由实测发现并修复的问题

### 1. 原生读取不响应取消，导致读取超时无效

首轮6项中有4项通过。ReceiveTimeout=250ms时，5秒后通道仍没有Closed；内存假传输支持取消，因此旧测试没发现。本机驱动路径的SerialPort.BaseStream.ReadAsync没有及时响应传入的取消信号。

修复：串口明确启用“读取期限到期时终止传输”策略。读取阶段的计时token到期，先原子保存TimeOut及日志，再关闭原生端口来解除挂起读取。注册严格限于读取阶段，读取结束后异步释放，避免管道Flush/背压误触发读取超时。普通TCP行为不变。

新增NonCancellableReadChannel回归测试：不理会token，直到Close才完成，确保没有虚拟串口的CI也能检出该问题。

### 2. 关闭后立即重开偶发拒绝访问

完整测试中复现COM1刚关闭后立即Open抛UnauthorizedAccessException，单独重置循环又可通过。这与Windows驱动独占句柄延迟释放现象一致；不把它武断解释为全部拒绝访问都可恢复。

新增SerialOpenRetryTimeout，默认500ms，0关闭。仅对一次性串口打开的UnauthorizedAccessException进行有界、可取消重试；时间用尽仍抛出原错误，不接管其他应用、不重发命令。SerialReconnect=true仍使用其原有连接恢复退避，二者不是命令重试。

测试中的独立原生Open探针也最多等待500ms释放窗口：验证最终释放，不声称Close返回后操作系统立即允许重开。永久占用或句柄泄漏仍会使测试失败。

新增普通测试覆盖：短暂拒绝后成功、禁用重试保留原异常、持续占用在期限后失败、Dispose中断打开重试。

## 重跑

确保COM1–COM2确为一对、没有其他应用占用：

```powershell
# 仅运行7项原生测试
./scripts/Test-VirtualSerial.ps1 -PortA COM1 -PortB COM2

# 全部测试，包含原生串口
./scripts/Test-VirtualSerial.ps1 -PortA COM1 -PortB COM2 -AllTests

# 全部测试并收集主库覆盖率
./scripts/Test-VirtualSerial.ps1 -PortA COM1 -PortB COM2 -CollectCoverage
```

也可以手动显式启用：

```powershell
$env:SUPERCLIENT_SERIAL_PORTS = 'COM1,COM2'
dotnet test IFactory.Interaction.SuperClient.slnx -c Release
```

脚本会恢复原有环境变量，测试日志/覆盖率在忽略的TestResults目录。默认普通CI不需要虚拟串口，也不会替用户安装驱动。

## 本次没有做的事

真实设备物理拔插、UART时序/校验错误、硬件流控/持续阻塞发送、其他驱动和操作系统、组播网络与长时压测未验证；按本次范围先搁置。显式SocketError关闭后的连接恢复不等于真实拔线验证。
