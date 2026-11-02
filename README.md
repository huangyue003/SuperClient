# SuperClient

独立的 .NET 10 通信库，支持 TCP 客户端自动重连、TCP 单连接服务器、UDP 和串口，使用 SuperSocket.ProtoBase 解包。

源工程复制自 `D:\Develop\BTS7_Master\IFACTORY\IFactory.Interaction.SuperClient` 及同级测试工程；源目录未修改。根目录构建文件参照 `D:\Develop\IFactory\EasyClient`。构建不依赖原解决方案或 EasyClient。

## 构建与测试

安装 .NET 10 SDK 后执行：

```powershell
dotnet restore IFactory.Interaction.SuperClient.slnx
dotnet build IFactory.Interaction.SuperClient.slnx -c Release
dotnet test IFactory.Interaction.SuperClient.slnx -c Release
dotnet build IFactory.Interaction.SuperClient.slnx -c Release -p:Platform=x86
```

`global.json` 允许使用已安装的稳定版 .NET 10 SDK feature band。框架和依赖版本分别在 `Directory.Build.props` 中声明，由 `Directory.Build.targets` 统一应用版本。支持 AnyCPU/x86；运行 x86 测试还需要对应架构的运行时。

## 使用

```csharp
await using var client = new SimplifiedClient(
    "tcp://127.0.0.1:9999/?Role=client",
    new FixedSizePipelineFilter(4));
await client.Initialization;
// Initialization 表示配置与创建器启动完成；TCP 连接可能仍在重试。
// 通过 ChannelCreated 回调或 Channel 属性确认连接就绪。
if (client.Channel is { IsClosed: false })
{
    var response = await client.SendReceiveAsync(new byte[] { 1, 2, 3, 4 }, 3000);
    if (response is not null) Console.WriteLine(Convert.ToHexString(response));
}
```

支持的 URI 示例：

- TCP 客户端：`tcp://localhost:9999`，可选 `Role=client`。
- TCP 服务器：`tcp://127.0.0.1:9999/?Role=server`，同时维护一个活动连接；关闭后接受下一连接。
- UDP：`udp://127.0.0.1:8888/?RemoteEndPoint=127.0.0.1:9999`。
- IPv4 UDP 组播：追加 `&Multicast=true`，本地地址应为对应网卡地址。
- 串口：`sio://COM2/?BaudRate=9600&Parity=None&StopBits=1&DataBits=8`。

公共 URI 参数 `ReceiveBufferSize`、`MaxPackageLength` 必须为正整数；参数名不区分大小写，支持百分号转义，拒绝重复或缺少等号的参数。

## 行为与迁移

- 使用 `Initialization` 观察配置或端口打开错误；无活动连接时发送会抛出异常。TCP 临时连接失败会记录日志并继续重试。
- `SendReceiveAsync` 超时或内置调度器遇到连接关闭时返回 `null`；`-1` 表示无限等待。超时计时从报文入发送管道后开始，不包含等待前一个无键请求和写入背压的耗时。
- 无键请求顺序执行；有键调度器支持不同键并发，相同键的重复在途请求会抛出异常。使用有键调度器时应传入完整发送包，原始字节重载没有可用于匹配的键。
- 无键协议无法识别“上一请求超时后才到达”的响应；有键协议也不应立即复用仍可能收到迟到响应的键。若业务要求严格隔离，应在协议中加入唯一请求编号，或在超时后重建连接。
- UDP 每次公共 `SendAsync` 调用发送一个数据报，最大负载 65507 字节；接收端按 pipeline filter 解包，UDP 本身不提供交付或顺序保证。
- `ChannelOptions` 默认最大包长 1 MiB、最大待消费包数 1024。超过限制或解包异常会关闭通道。直接构造通道时可调整 `MaxPendingPackages`；慢处理器应避免长时间阻塞接收调度。
- `ReceiveTimeout`、`SendTimeout` 单位为毫秒，0 表示禁用，作用于底层 I/O。串口驱动不一定支持异步取消，关闭会释放端口；串口 `DetachAsync` 明确不支持。
- 每个通道只支持一个包流消费者。`StartRecive()` 为兼容旧调用保留，接收循环由客户端自动启动。
- 外部传入的 `In`/`Out` 管道必须满足单读者、单写者约束；`In` 的暂停阈值需要足以容纳最大未完整报文。UDP 应使用公共发送方法保留调用边界。
- 自定义调度器负责自身超时和断连清理；不要在回调中同步阻塞等待同一客户端的异步生命周期操作。

完整修复记录和验证边界见 [REVIEW.md](REVIEW.md)。
