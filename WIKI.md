# SuperClient Wiki

SuperClient 是基于 .NET 10 的异步通信库，统一 TCP、UDP 和串口的连接管理、协议解包及请求响应处理，适用于设备通信与自定义二进制协议应用。

## 主要能力

- TCP 客户端自动重连，以及逐个处理连接的 TCP 单连接服务器。
- UDP 单播与 IPv4 组播配置。
- 串口通信及显式开启的连接恢复。
- 固定长度、固定头和分隔符解包，可扩展自定义过滤器与编码器。
- 无键串行请求响应，以及按请求键匹配的并发请求响应。
- 发送预算、接收空闲超时、有界接收队列、关闭原因和结构化日志。

## 快速开始

安装 .NET 10 SDK，在仓库根目录执行：

```powershell
dotnet restore IFactory.Interaction.SuperClient.slnx
dotnet build IFactory.Interaction.SuperClient.slnx -c Release
dotnet test IFactory.Interaction.SuperClient.slnx -c Release
dotnet run --project examples/LoopbackDemo/LoopbackDemo.csproj -c Release
```

回环示例无需外部设备，演示建立连接、发送四字节请求、接收回显与释放资源。其他应用可通过项目引用使用 `IFactory.Interaction.SuperClient.csproj`。

## API 选择

| 使用场景 | 推荐入口 |
| --- | --- |
| 简单字节协议 | SimplifiedClient |
| 自定义收发对象和请求键 | SuperClient<TSendPackage,TReceivePackage> |
| 无请求编号，逐个请求响应 | ImmediatePackageScheduler |
| 有唯一请求编号，并发匹配响应 | KeyPackageScheduler |
| 直接管理连接和消费包流 | TcpChannelCreator、UdpChannelCreator及通道接口 |

连接地址示例：

```text
tcp://localhost:9999/?Role=client
tcp://127.0.0.1:9999/?Role=server
udp://127.0.0.1:8888/?RemoteEndPoint=127.0.0.1:9999
sio://COM2/?BaudRate=9600&Parity=None&StopBits=1&DataBits=8
```

地址和端口为示例，按实际部署替换。串口参数示例使用 Windows 端口命名。

## 使用约定

`Initialization` 表示配置和创建器启动完成；通过 `Channel` 或 `ChannelCreated` 判断连接是否就绪。发送完成表示底层传输 API 已接受全部字节，不代表设备已收到或执行。库不会自动重放业务命令。

`SendTimeout` 控制发送准备、等锁与底层发送预算；`SendReceiveAsync` 的响应期限从发送完成后开始。`ReceiveTimeout` 控制单次读取空闲等待，并非完整业务事务期限。

串口恢复默认关闭。开启 `SerialReconnect` 后恢复连接；如需在队列溢出等 `InternalError` 后恢复，还需显式开启 `SerialReconnectOnInternalError`。本地、应用和协议关闭仍停止恢复。应按报文大小与消费速度设置 `MaxPackageLength` 和 `MaxPendingPackages`。

使用 `await using` 或显式异步关闭释放连接。逐连接诊断优先使用 `Disconnected` 事件中的通道、标识和关闭原因快照。

## 项目结构与深入阅读

主库目录负责连接、管道、解包和调度；测试目录覆盖单元测试、TCP/UDP 回环及可选原生串口测试。`examples/LoopbackDemo` 提供可运行示例，`scripts` 提供覆盖率、故障变异和串口验证工具。

仓库根目录中的文档：

- `README.md`：完整参数、发送契约和兼容约定。
- `PROJECT-GUIDE.md`：源码结构、数据流与扩展入口。
- `TEST-COVERAGE.md`：覆盖率与补测评估。
- `SERIAL-VALIDATION.md`：可选虚拟串口验证方法。
- `RECONNECT-REVIEW.md`：重连策略修复与 x64/x86 验证记录。

普通测试不自动占用串口；原生串口测试需明确配置一对可用端口。虚拟串口验证不能替代真实设备、其他操作系统及长时负载验收。
