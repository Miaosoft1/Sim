# Miaosoft.Sim

[English](README.md)

模块化、事件驱动的模拟框架：通过类型化内容组合战斗规则，关注明确的事件顺序、
独立的运行状态、原子能力变更和可验证的行为。

**尚在提取中，不是正式发布版。** Loadout 原语、真实回环网络／Console 原型及
私人源码引用已验证；私人模式接入覆盖和最终公开源码／许可证核查仍在进行；
不包含图形客户端。
公开框架拟采用 [MIT 许可证](LICENSE)；发布前仍须[核实依赖及内容的再分发权利](docs/zh-CN/dependencies.md)。

## 架构

```text
示例 / 应用组合
    |-- Content.Loading -> Content.Core
    |-- Combat.Skill.Content -> Combat.Skill -> Combat.Map -> Core
    |-- Combat.Map.Content   -> Combat.Map
    |-- 可选 Loadout.Content -> Loadout -> Combat.Skill
    |-- Server.Hosting -> Runtime -> Protocol -> Core / Serialization.Yaml
    |-- Server -> Runtime.Abstractions / Combat.Skill
    `-- Console -> Client -> Control -> Protocol
```

- **Core**：模拟时间队列、事件接收器、取消和原子变更。
- **Combat.Map**：地图实例、几何、移动和空间选择。
- **Combat.Skill**：能力、投递与效果、激活门控、充能和触发器。
- **Content**：通过 DI 显式选择模块，编译目录局部的不可变定义；
  安装时才创建新的运行实例，Combat 不依赖 YAML 加载。
- **Loadout**：可选库存、角色保留天赋树及原实例原子交换。
- **Sessions**：单执行者、凭据绑定连接、准备／确认快照及重试确认；
  Client／Console 构建闭包不包含服务端实现。

当前事件链路、内容边界及尚未迁移的部分见[架构与执行模型](docs/zh-CN/architecture.md)。

## 运行与验证

使用 [global.json](global.json) 指定的 .NET SDK。在当前目录**串行**执行：

```powershell
dotnet run --project .\samples\EventScheduling\EventScheduling.csproj -- --output Detailed
dotnet run --project .\samples\LocalCombat\LocalCombat.csproj -- --output Detailed
dotnet run --project .\samples\NetworkSession\NetworkSession.csproj -- --output Detailed
dotnet run --project .\tests\Skill\Miaosoft.Sim.Combat.Skill.Tests.csproj -- --output Detailed
dotnet run --project .\tests\Content\Miaosoft.Sim.Content.Tests.csproj -- --output Detailed
```

前两个是可执行 MSTest 示例，不是交互式客户端。EventScheduling 展示顺序、
延迟和取消；LocalCombat 展示 YAML 加载、投射物→伤害→治疗、有限充能、
状态叠层和到期、独立场景及表达式正反例。除汇总结果外，还应阅读每项测试的输出。

NetworkSession 自行启停真实本机回环监听，验证战斗、移动、状态到期、在途重连、
精确 1024 条确认窗口及模块隐私、棋盘／入口几何及不重播的瞬时投射物消息，
也展示可选 Loadout 交换及完成态死亡授权领取。
基础应用不依赖 Loadout。交互体验时，在不同终端启动服务端和**两个**控制台：

```powershell
dotnet run --project .\apps\Server\Miaosoft.Sim.Server.App.csproj -- --Demo:Enabled=true
dotnet run --project .\apps\Console\Miaosoft.Sim.Console.App.csproj
```

Alpha 控制台可输入 `cast bolt Beta`，再使用 `status`、`disconnect`、`rejoin`。
`bolt` 有两次充能，`moving-bolt` 展示前摇及可见轨迹。
详见[网络契约及当前限制](docs/zh-CN/networking.md)；
入队确认不等于执行成功。

## JavaScript 客户端

浏览器与 Node.js 共用的 ES module SDK 位于
[clients/javascript](clients/javascript)。它实现隔离会话协议原型：
演示入场／重连、YAML 事件信封、快照更新、队列收据及原始重试保留。
SDK 本身没有服务端实现依赖，也不使用 Node-only 运行时 API。
Node.js 只用于安装依赖和运行可选的 Vitest 测试：

```powershell
cd .\clients\javascript
npm install
npm test
```

它不是图形客户端，也没有启用私人 Loadout 或私人认证行为。

LocalCombat 命令用 `AbilityInstanceId` 选择实际运行实例。同定义存在多份已安装
命令能力时，缺少选择返回 `AmbiguousAbility`；未知、其他持有者、定义不匹配或
未授权实例返回 `AbilityNotAvailable`。选中的实例充能耗尽时不会回退到另一份。
网络原型也使用真实运行实例寻址命令。

## 参考

详见[触发条件及表达式参考](docs/zh-CN/trigger-conditions.md)。
独立私人应用的接入方式见[源码引用与扩展边界](docs/zh-CN/private-consumption.md)。
