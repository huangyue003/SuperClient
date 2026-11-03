# SuperClient

独立的 .NET 10 通信库，支持 TCP 客户端自动重连、TCP 单连接服务器、UDP 和串口，使用 SuperSocket.ProtoBase 解包。源工程复制自 `D:\Develop\BTS7_Master\IFACTORY`，原工程未修改；构建不依赖旧解决方案或 EasyClient。

## 文档与快速体验

- [项目结构、数据流与API选择](PROJECT-GUIDE.md)
- [当前覆盖率与还需补充的测试](TEST-COVERAGE.md)
- [本轮代码审查与修复结论](CODE-REVIEW.md)
- [COM1–COM2 原生虚拟串口验证与重跑方法](SERIAL-VALIDATION.md)

无需设备的TCP回环示例：`dotnet run --project examples/LoopbackDemo/LoopbackDemo.csproj -c Release`。

## 构建与验证

```powershell
dotnet restore IFactory.Interaction.SuperClient.slnx
dotnet test IFactory.Interaction.SuperClient.slnx -c Release
dotnet build IFactory.Interaction.SuperClient.slnx -c Release -p:Platform=x86
dotnet test IFactory.Interaction.SuperClient.slnx -c Release --no-build
dotnet publish IFactory.Interaction.SuperClient/IFactory.Interaction.SuperClient.csproj -c Release -p:PublishProfile=FolderProfile
```

`global.json` 选择稳定版 .NET 10 SDK。框架、公共编译设置和包版本集中管理。产物分别位于 `bin/AnyCPU/Release/net10.0/`、`bin/x86/Release/net10.0/`；中间目录也按平台隔离。x86 构建不会覆盖默认测试产物。运行 x86 测试需要 x86 .NET 10 运行时。发布配置输出到对应平台目录下的 `publish/`。

可重复的深度检查：

```powershell
./scripts/Measure-Coverage.ps1
./scripts/Test-QualityMutations.ps1
./scripts/Measure-SendLatency.ps1 -Before c27f8c0 -After HEAD
```

脚本只修改系统临时目录中的 Git 快照，不修改工作区源码；结果写到被忽略的 `TestResults/`。

## 使用与连接状态

```csharp
await using var client = new SimplifiedClient(
    "tcp://127.0.0.1:9999/?Role=client",
    new FixedSizePipelineFilter(4),
    new ChannelOptions { SendTimeout = 2000, ConnectTimeout = 5000 });
await client.Initialization;
if (client.Channel is { IsClosed: false })
{
    var response = await client.SendReceiveAsync(new byte[] { 1, 2, 3, 4 }, 3000);
    if (response is not null) Console.WriteLine(Convert.ToHexString(response));
}
```

`Initialization` 表示配置与创建器启动完成；TCP 可能仍在连接，显式启用串口重连时也可能仍在尝试打开端口。通过 `Channel` / `ChannelCreated` 判断就绪。配置失败可通过 `Initialization` 观察。无连接时发送抛 `InvalidOperationException`。

服务器可以绑定 `tcp://127.0.0.1:0/?Role=server`，在 `Initialization` 完成后、第一连接到来之前通过 `client.LocalEndPoint` 获取真实监听地址。服务器同时维护一个活动连接，关闭后接受下一连接。

新增 `Disconnected` 事件携带已关闭通道的快照、Identity 和 Reason；原 `ChannelDisconnected` 保留。`LastCloseReason` 供状态查询，`LastSendTime` / `LastReceiveTime` 在通道上分别表示两个方向的活动；未发生时为 null。

## URI 与选项

- TCP 客户端：`tcp://localhost:9999`，可选 `Role=client`；客户端支持DNS主机名。
- TCP 服务器：`tcp://127.0.0.1:9999/?Role=server`。
- UDP：`udp://127.0.0.1:8888/?RemoteEndPoint=127.0.0.1:9999`。
- IPv4 UDP 组播：追加 `&Multicast=true`，本地地址为相应网卡地址。
- 串口：`sio://COM2/?BaudRate=9600&Parity=None&StopBits=1&DataBits=8`。

TCP 本地监听地址、UDP 本地与 RemoteEndPoint 必须为 IP 字面量，不解析主机名。无效 URI 或端点配置抛带上下文的 ArgumentException；端口占用、权限等真实运行错误不伪装成参数错误。IPv6 单播支持对应地址族；IPv6 组播不支持。

参数名不区分大小写，支持百分号转义，拒绝重复参数及缺少等号。选项可通过 `ChannelOptions` 重载提供；创建时复制配置，URI 中已识别的参数优先。不要在运行期间修改直接交给自定义通道的选项。

| 参数 | 默认值 | 含义 |
| --- | --- | --- |
| MaxPackageLength | 1048576 | 最大接收帧及单次公共发送负载，正整数 |
| MaxPendingPackages | 1024 | 待消费包数上限，正整数 |
| ReceiveBufferSize / SendBufferSize | 4096 | 接收内存请求/缓冲段设置，正整数；UDP接收强制至少65536 |
| SendTimeout | 0 | 单次发送准备、等发送锁、底层发送的毫秒预算；0禁用 |
| ReceiveTimeout | 0 | 单次底层读取等待预算；0禁用 |
| ConnectTimeout | 5000 | 单次TCP连接尝试预算；0禁用 |
| ReadAsDemand | false | 按消费需求触发读取；不保证一个输入块中的多个包永不溢出 |
| ReconnectMinDelay / ReconnectMaxDelay | 200 / 5000 | 连接失败指数退避的初始值与上限，毫秒 |
| ReconnectJitter | true | 加入抖动，实际延时不超过上限 |
| SerialOpenRetryTimeout | 500 | 一次性打开串口时，拒绝访问的有界重试窗口；0禁用 |
| SerialReconnect | false | 显式开启串口连接恢复；不重发任何指令 |
| NoDelay / KeepAlive | false / false | 可配置TCP套接字选项；不设置ReuseAddress或强制Linger |

串口默认参数为9600、8数据位、None校验、One停止位；必须提供真实端口名。保留合法数值枚举写法，包括现有的 `StopBits=1`；未定义的值会被拒绝并列出允许名称。无参端点对象用于配置，未填写端口名之前不应拿去打开设备。

TCP 失败重试有上限退避，首次、数量里程碑、定期汇总记录Warning，恢复记录Information。串口恢复默认禁用；开启后仅连接/读取类故障触发恢复，本地关闭、应用/协议/容量错误停止恢复。

## 发送完成、超时与失败

**本轮行为变更：公共 SendAsync 返回代表本次全部字节已被底层传输 API 接受。TCP/串口不再仅等待写入本地管道。它不保证对端收到或执行；库绝不自动重发命令。**

| 场景 | 调用方观察 |
| --- | --- |
| 发送开始前或等锁时通道已关闭 | InvalidOperationException |
| 等发送锁超时 | TimeoutException；本次未发送，不关闭其他正在发送的连接 |
| 实际发送超时 | TimeoutException，Data含BytesAccepted/BytesPending；通道以TimeOut关闭 |
| 发送途中关闭/传输故障 | ChannelTransportException，含Reason、已接受/待发字节数、原始异常 |
| 编码失败或负载超限 | 原始编码异常/ArgumentException；未开始发送，不破坏健康连接 |

发送失败日志包含Identity、阶段、原因和字节数；观察者异常也会记录。对端响应丢失、发送中断或发送后断连，都可能对应“命令已经部分或全部发出”，不能无条件重试。

原始字节负载直接交给传输。编码器/Action使用受限的私有池化Pipe分段缓冲，在发送完成之前保留段；超过MaxPackageLength（UDP还限制65507）即拒绝，不进行整帧ToArray拷贝。调用完成之前不要修改传入的字节内存。

SendTimeout 的计时包含同步编码/写入耗时，但同步用户代码不能被强行中断，只能在回调返回后观察超时。通道关闭不会等待一个永不返回的用户写入回调；该回调自己的任务仍需业务释放。底层驱动若不支持取消，也不能承诺毫秒级硬实时返回。

`SendReceiveAsync(millisecondsTimeout)` **仅控制底层发送完成之后的响应等待**，不包含前一个串行请求的等待和发送预算；-1表示无限等待。发送阶段失败会抛异常；进入响应等待之后，超时或内置调度器遇到断连返回null。总耗时还可能包含无键请求排队，没有固定的“SendTimeout+响应超时”总上限。

串口读取超时会先记录TimeOut，再关闭端口以解除驱动可能忽略取消的读取；刚关闭的端口允许在SerialOpenRetryTimeout窗口内重试打开，持续占用仍报错。

ReceiveTimeout 是读取空闲等待期限：即使协议正确，连接长期没有新字节也会以TimeOut关闭，并可能触发TCP重连。它不是整个报文组装或业务事务的期限。

## 调度、容量与兼容

无键请求串行执行，有键调度器支持不同键并发，同键重复在途请求拒绝。独立自定义调度器可实现公开的 `ISerializedRequestScheduler` 声明串行要求；自定义实现仍负责自身断连清理。内置调度器的 `UnmatchedPackages` 是未匹配计数，合法主动上报被回调处理也可能被计入，不等于丢包。

无键协议无法识别上一请求超时后的迟到响应；有键协议也不应立即复用仍可能收到迟到回应的键。严格隔离需要唯一请求号或明确的超时后重建连接策略。

队列容量溢出保持fail-fast，以InternalError关闭并记录pending/capacity；协议解码错误以ProtocolError关闭。本版不默认切换为接收背压。根据实际报文大小调小容量：

| 最大报文 × 待消费包数 | 排队报文负载估算 |
| --- | --- |
| 1MiB × 1024（兼容默认） | 约1GiB |
| 64KiB × 128 | 约8MiB |
| 4KiB × 128 | 约512KiB |

这是负载估算，不是连接或进程的严格内存上界；还有管道、临时编码和解码后对象图开销。

内置TCP/UDP/串口的DetachAsync均在改变状态前抛NotSupportedException，请使用CloseAsync。每个通道仅一个包流消费者；旧StartRecive、公开setter和拼写兼容成员保留，避免无关破坏性API变更。

高级In/Out管道仍要求单读者、单写者；通过IPipeChannel.Out写入是低层管道入口，不具有公共SendAsync的逐调用完成确认。自定义In暂停阈值必须足够容纳未完整帧；不要跨重连复用已经完成的自定义Pipe。UDP请用公共发送方法保留数据报边界。

历史记录见 [REVIEW.md](REVIEW.md) 和 [FIX-VALIDATION.md](FIX-VALIDATION.md)，当前复审结果见 [CODE-REVIEW.md](CODE-REVIEW.md) 与 [TEST-COVERAGE.md](TEST-COVERAGE.md)。
