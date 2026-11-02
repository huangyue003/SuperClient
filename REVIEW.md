# 审查与修复记录

审查日期：2026-11-03。审查范围包括两个工程全部源码、测试、构建文件及发布配置。按“阅读与定位 → 修复 → 新增回归测试 → 再次审查”的方式进行了多轮处理。本记录不是不存在其他缺陷的保证。

## 独立化与升级

- 复制主工程及测试工程，排除 bin/obj 等生成目录，原工程未修改。
- 新建 `.gitignore`、`Directory.Build.props`、`Directory.Build.targets`、`.slnx` 和 SDK 约束文件。
- 统一到 net10.0；发布配置中遗留的 net6.0 同步升级。
- Microsoft.Extensions.Logging.Abstractions 5.0.0 → 10.0.14。
- SuperSocket.ProtoBase 2.0.0-beta.31 → 2.1.0。
- System.IO.Ports 6.0.0 → 10.0.14。
- Microsoft.NET.Test.Sdk 17.12.0 → 18.11.0。
- xunit 2.9.2 → 2.9.3；xunit.runner.visualstudio 2.8.2 → 4.0.1。
- 移除 .NET 10 已提供的 System.IO.Pipelines 包引用、未使用的 JetBrains.Annotations 和过时的 dotnet-xunit CLI 工具引用。
- 移除空的内部 SandClock 类，消除可空性和未使用成员警告。

NuGet 官方源的 outdated 检查未报告任何保留包存在更新；含传递依赖的漏洞检查未报告已知漏洞。检查仅反映执行时源提供的数据。

## 已处理问题

| 范围 | 原问题及结果 |
| --- | --- |
| 固定头解包 | AdvanceToEnd 吞掉粘连的下一报文；改为只推进当前包长度。 |
| 固定长度/固定头解包 | 从整个 Sequence 起点切片、使用总长度判断，忽略 reader 当前 Position；改用 Position/Remaining。 |
| 解包参数 | 零或负固定长度、空分隔符可能无进展；增加校验；分隔符复制后保存，避免外部数组修改。 |
| 固定头长度 | 头长加体长可能整型溢出；使用 checked，解包异常关闭通道。 |
| TCP 发送 | Socket 部分发送后整段缓冲被丢弃；按实际发送量循环推进。直接使用 Memory API，不再依赖数组后备内存。 |
| UDP 发送 | 多段缓冲使用未连接 Socket.SendAsync；现合并为单个数据报并使用 SendToAsync。公共发送调用保留各自数据报边界。 |
| UDP 接收 | 默认 4 KiB 接收内存会截断大数据报；确保至少 65536 字节；合法空数据报不会被误当成连接关闭。 |
| UDP 生命周期 | 未连接 socket 上 Shutdown 抛错、Windows IOControl 无条件调用、创建失败资源未释放；改为直接 Dispose、限定平台并在失败路径释放。 |
| UDP 组播 | 网卡索引查找脆弱且缺少配置校验；使用本地 IPv4 地址选择网卡，明确拒绝未支持的 IPv6 组播。 |
| URI 参数 | Split('&', 2) 无法完整解析第三个及后续参数，空查询或缺少等号导致越界；统一解析、转义、重复参数和正整数校验。 |
| TCP 默认角色 | 缺少 Role 时可能对 null 调用 ToLower；改为明确的 client 默认值和角色校验。支持客户端 DNS 地址与 IPv6 socket。 |
| TCP 重连 | 阻塞 Connect/Accept、Thread.Sleep、失败 socket 泄漏、Dispose 不释放活动 socket；改为可取消异步循环、关闭监听和活动 socket、重连前重置 filter。 |
| 连接初始化 | 构造函数丢弃启动异常；新增 Initialization 任务供调用方观察，Reset 可恢复初始化失败。 |
| 客户端重置 | 同步信号量与 Sleep 阻塞线程，重置和释放竞争；异步串行化生命周期，使用代次拒绝过期创建回调。 |
| 接收循环 | async void 异常无法观察、HandlePackage 未 await、可重复启动；改为任务接收循环并等待调度器，处理和记录异常。 |
| 请求发送 | 断连时静默跳过发送；改为明确失败，注册 awaiter 后固定使用同一通道，发送失败清理注册。 |
| 请求等待 | 每次 Task.Run 阻塞线程和 AutoResetEvent 句柄；改用异步 TaskCompletionSource 和 WaitAsync。只接受第一次响应。 |
| 请求匹配 | 无键 awaiter 被并发覆盖；无键请求串行化，直接重复注册拒绝。有键重复键拒绝，不再覆盖旧等待者。 |
| 超时/断连 | 超时等待者滞留、无限等待无法释放；超时条件删除自身注册，内置调度器在断连/释放时完成等待者。 |
| EOF 竞态 | 关闭可能先于最后一个已解码响应交付；先排空包流再清理远端断连等待者；代次隔离重置前的包。 |
| 对象队列 | 手工池数组及可重用 ValueTaskSource 存在释放、并发和续体重入风险；改为框架 Channel，明确完成、排空和单消费者语义。 |
| 内存边界 | 解码包无限积压；增加 MaxPendingPackages 上限，溢出关闭并记录协议错误。 |
| 包长保护 | 有状态 filter 分批消耗的总长度未累计；累计当前帧消耗；拒绝无进展 filter 和无进展切换循环；不完整 EOF 视为协议错误。 |
| 背压 | 默认 64 KiB 输入管道阈值使大于阈值的完整帧无法完成；按最大包长设置阈值。关闭取消挂起的 Read/Flush，并在 finally 完成管道。 |
| 按需读取 | 分片输入可能耗尽一次读取需求后永久等待；未完成帧继续发出读取需求；关闭唤醒等待者。 |
| 发送错误 | 编码异常被吞掉或留下半个包；先编码到独立缓冲再写入；Action 写入失败关闭通道并传播异常。 |
| 关闭事件 | 竞争可重复通知、串口提前设置 IsClosed 跳过事件；统一只通知一次；单个观察者异常不妨碍其他观察者和清理。 |
| 超时/状态 | 通道超时选项未使用、端点和 LastActiveTime 缺失；底层 I/O 应用超时，网络通道记录端点，客户端保留最近活动时间。 |
| 串口配置 | StopBits 已解析却未传入构造函数；现在完整传入。校验波特率、枚举、数据位，默认 StopBits.One。 |
| 串口取消 | 部分驱动无法取消读取；关闭时释放端口；无法可靠保留端口的 Detach 显式拒绝，避免无限挂起。 |
| 原始测试 | 无限循环、固定端口、无断言、依赖 COM 设备及远端服务；替换为有超时与断言的 loopback/内存回归测试。 |

## 验证

- 自动测试覆盖解包、调度、生命周期、TCP/UDP loopback、TCP 100000 字节收发、UDP 60000 字节接收、多段 UDP 发送、TCP 重连及服务器下一连接。
- 底层内存通道覆盖每次只发送 3 字节、零发送计数、分片按需读取、超长与无进展 filter、累计包长、背压关闭、接收超时和队列容量。
- Debug：42 项测试通过，0 失败、0 跳过。
- Release：42 项测试通过，0 失败、0 跳过。
- Release/x86：构建成功，0 警告、0 错误；已核实 PlatformTarget=x86。
- NuGet：无过期的直接引用包，直接及传递依赖均未报告已知漏洞。

包版本可对照官方页面：[SuperSocket.ProtoBase](https://www.nuget.org/packages/SuperSocket.ProtoBase)、[Logging.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions)、[System.IO.Ports](https://www.nuget.org/packages/System.IO.Ports)、[Test SDK](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk)、[xunit](https://www.nuget.org/packages/xunit)、[VS runner](https://www.nuget.org/packages/xunit.runner.visualstudio)。

## 仍需外部条件确认的边界

1. 没有真实串口设备/虚拟串口对，因此仅验证串口配置解析；驱动读写、断线、端口释放延时需要目标设备验证。
2. 没有指定组播网络环境；未实测跨网卡、路由器、组播成员与防火墙行为。IPv6 组播明确不支持。
3. 无唯一请求编号的协议无法可靠区分迟到响应；应用应升级协议或采用超时后重建连接策略。不能由通用库猜测修正。
4. 自定义 scheduler/filter 的业务正确性、线程安全和取消策略由其实现保证；同一个可变 filter 不应跨独立客户端共享。
5. 未在 Linux/macOS、真实远端高负载网络或 x86 运行时执行测试；本机验证环境是 Windows x64/.NET 10。x86 仅进行构建验证。
