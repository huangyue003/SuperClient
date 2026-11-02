# 本轮修复与验证（2026-11-03）

## 实施决策与行为变化

- B1：TCP/串口的公共 SendAsync 现在等待本次全部字节被底层接受；不保证对端收到或执行，不自动重发。原始字节直接发送；编码器/Action 使用有硬容量限制的池化 Pipe 分段缓冲，发送完成前保留段，不再 ToArray 全量复制。
- SendTimeout 是单次调用的准备、等锁、底层发送预算。同步用户编码器/Action无法被强行中断：计时包含其耗时，但超时只能在回调返回后被观察。排队超时尚未发送任何字节，不关闭其他正在发送的连接；发送中超时则关闭连接，避免残留半帧继续复用。
- Q1/Q2：只忽略生命周期已关闭时的预期取消。局部超时token到期且生命周期未取消，转换为 TimeoutException；保存首次通道故障后才取消其他操作。传输故障使用 ChannelTransportException，包含原因、已接受/待发送字节数和原异常。发送超时日志为Error，含Identity、阶段、原因、字节数。
- Q3：SendReceiveAsync的millisecondsTimeout继续仅控制响应等待，不含串行请求排队与发送。总时长不是一个固定上界：还包括排队，且SendTimeout=0不限制发送。不能声称“总上限恒等于SendTimeout+timeout”。
- Q4：每帧发送容量受MaxPackageLength（UDP还受65507）限制；GetMemory请求和Advance均先校验容量。暂不做额外的缓冲池/散聚写微优化。
- 原测试迁移：ZeroSendCountClosesInsteadOfSpinning 必须改为断言 ChannelTransportException。旧断言隐含“底层失败仍正常返回”，与B1约定冲突；仍保留SocketError关闭原因断言。其余原有断言暂未改动。
- 公共setter、旧拼写和扩展点保留；不无条件启用ReuseAddress；不默认开启串口重连。CI与广泛warnings-as-errors本轮不擅自开启。

后续修复、验证和保留项在验收完成后追加。
