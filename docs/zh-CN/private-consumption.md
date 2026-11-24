# 私人应用与源码引用

[项目概览](../../README.zh-CN.md) · [English](../en/private-consumption.md)

## 两份独立源码树

公开框架与私人应用仓库保持相邻：

```text
repos/
  Miaosoft.Sim/              公开框架与中性示例
  Private.Game/          私人模式、应用、内容与资源
```

公开树不得反向引用私人树。私人玩法、Godot、生成资源、凭据及 AI 工具不进入公开目录，
继续遵守私人仓库原有的跟踪／忽略规则。不要复制私人 Git 历史，也不要通过
`assume-unchanged` 把公开源码目录里的私人文件“藏起来”。

相邻公开目录已经独立验证，现有私人应用也已切换为相对引用。旧共享源码仍保留在
私人检出中但已退出活动 solution；新的实现以相邻公开程序集为准。这些说明不自动
初始化公开 Git、创建提交／远程仓库或发布包。

## 相对 ProjectReference

对于位于 `apps/Console/Console.csproj` 的私人项目，基础 SDK 引用为：

```xml
<ProjectReference Include="..\..\..\Miaosoft.Sim\clients\csharp\Miaosoft.Sim.Client.csproj" />
```

如果该应用选择 Loadout，再显式引用可选客户端：

```xml
<ProjectReference Include="..\Miaocrosoft.Script\src\BattleGame\Client\Loadout\Miaosoft.Sim.Loadout.Client.csproj" />
```

位于 `src/Game/Modes/Example/Example.csproj` 的私人玩法项目可引用 Combat：

```xml
<ProjectReference Include="..\..\..\..\..\Miaosoft.Sim\src\Combat\Skill\Miaosoft.Sim.Combat.Skill.csproj" />
```

路径从各自项目文件计算，不从解决方案或命令行当前目录计算。
不要引用会话目录、复制的 DLL 或其他检出目录的 `bin`。
私人解决方案可纳入实际依赖的公开项目以便联调；这不合并仓库，也不改变依赖方向。

## 归属与扩展点

- 私人模拟适配器实现 `Runtime.Abstractions` 的 `ISessionSimulation`；
  仅由应用创建 `SessionRuntime` 执行者。
- 生产私有接入保留私有语义：校验分配 owner、generation 和 ticket，先提交参与权，
  再将每个 admitted seat 绑定到权威公共 session credential；生产不能启用 demo
  admission。旧客户端协议桥接不得把入队确认伪装成执行成功。
- 私人网络命令通过按方向注册的 codec 接入；服务端适配须绑定当前参与者，校验权限和版本。
- 两端显式选择观察模块；分离不可变状态与世界同帧提交，失去控制时移除私有数据。
- 通过 DI 注册所选内容模块及类型化公式字段；玩法目录和资源保留在私人仓库，
  不修改全局 YAML 可变状态。
- Loadout 在程序集级别可选；树授权、库存、交易仍独立组合，不成为基础 Combat 的依赖。

迁移会把旧通用源码保留为不参与构建的历史副本，而不是删除。
不得同时编译新旧 Context／Combat，也不能靠重建已转移能力绕过不兼容。
私人处理器、内容及延迟重放编排需要真正适配和回归；仅替换命名空间不是完成迁移。

## 版本与验证

项目引用使用当前检出的源码，**不会固定 Git 提交**。
待公开树有获准创建、验证通过的真实提交后，在私人仓库记录确切 SHA，
由私人 CI 将该版本显式检出到相邻目录。在此之前应明确依赖**未锁定**，
不编造提交号或宣称已经可复现发布。

先运行根目录的公开验证命令，再构建／测试受影响的私人应用和玩法。
跨边界保留原物品身份、冷却、充能、角色天赋进度、接入／参与权及恢复语义。
公开交付不得夹带私人内容或未经核查的生成产物。
