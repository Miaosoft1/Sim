# 架构与执行模型

[项目概览与运行命令](../../README.zh-CN.md) · [English](../en/architecture.md)

本文描述当前隔离提炼工程，并非已发布的 API。真实回环／Console 原型已验证；
网络剩余集成、私人项目集成，以及最终源码和许可证检查仍未完成。

## 分层

| 层 | 职责 |
|---|---|
| [Core](../../src/Core) | Context、事件调度、接收器、取消和原子事务。 |
| [Combat.Map](../../src/Combat/Map) | 地图定义与实例、移动、空间对象和可见性。 |
| [Combat.Skill](../../src/Combat/Skill) | 单位、能力、目标选择、激活门控、效果及触发路由。 |
| [Content.Core](../../src/Content/Core) 和 [Content.Loading](../../src/Content/Loading) | 内容契约、模块注册与目录组合。 |
| [Skill.Content](../../src/Combat/Skill.Content) 和 [Map.Content](../../src/Combat/Map.Content) | 将选定的 YAML 格式编译为运行时领域定义。 |

内容适配器依赖运行时领域，运行时不反向依赖内容。宿主显式选择模块；各目录独立，
不共享可变的作者态。编译后的能力定义可复用，每次安装则创建运行时 handler 和状态。
每个 Context 都是独立模拟，只有一个逻辑执行者。

`AbilityHandlerDefinition.Type` 保留规范化后的注册内容类型，宿主无需创建处理器或
反射其实现类型即可验证构筑／控制契约。编译器擅自改变类型会在加载时被拒绝，
错误包含源文件与能力 ID。

扩展编译器可在加载阶段用 `AbilityParserContext.CompileTrigger` 将附加触发器编译为
不可变定义，沿用该事件的条件 schema 和来源引用检查；安装工厂用
`AbilityHandlerFactory.CreateTrigger` 创建具名触发器并绑定本次能力的真实运行身份。
多个触发器可以调用同一能力，但不能共享上次安装的 handler，或在工厂闭包中保留作者 DTO／加载上下文。

## 事件与处理器

Context 拥有模拟时间队列。`Dispatch` 只将工作入队；`RunUntil` 等运行器推进队列并
调用已注册处理器。[调度示例](../../samples/EventScheduling/EventSchedulingSample.cs)
展示优先级、入队顺序、延迟、作用域取消，以及不受取消影响的后续事件。
入队并不等于执行。

能力安装和移除通过事件请求，单位上的安装器协调变更；原子事务避免 handler 和状态
只部分更新。触发器判断配置的事件并发出调用事件，不直接调用其他 handler，也不持有
响应能力的可变状态。

当前单次发布路由覆盖命令、投射物命中及爆炸、伤害请求、生命扣减、待确认及已确认死亡、
治疗请求、信号、周期伤害/治疗 tick。接收器投递不是新的语义事件。实际分发时捕获
候选监听器，执行时重新验证注册；即使命令 ID 和时间戳相同，不同真实事件也各自保留。

伤害接纳是单独排队的请求，不是语义发布。接纳时决定是否由活动结算延迟；否则
排入发布事件，之后创建的结算不会追溯延迟已接纳的请求。延迟请求只在重放时发布；
只能从活动结算中取出原事件、使用原接收器重放一次。完成仅等待重放相关处理器调用，
不保证所有派生事件或死亡连锁完成；进行中的重放被独立取消时明确报告失败。
可选的同 Context 完成接收器在处理器调用后收到 `DeferredDamageReplayCompletedEvent`，
供事件驱动的宿主安排后续重放。宿主仍须单独验证其派生链收尾顺序，不可仅凭一次
重放完成就断言所有派生事件或死亡连锁均已结束。

## 战斗链路

[LocalCombat 内容](../../samples/LocalCombat/combat.yaml)和
[可执行场景](../../samples/LocalCombat/LocalCombatSample.cs)显式加载选定模块，
由两个独立 Context 共用一份编译后的目录：

1. 命令以定义 ID 和运行实例 ID 选择 `bolt`。
2. 投射物命中；`bolt-damage` 求值条件，经目标接收器应用配置伤害。
3. 生命扣减事件触发 `heal-delivery`，其命中再激活 `heal-payload`。
4. 测试验证伤害、治疗、充能耗尽和不同场景的能力实例身份；请同时查看详细输出。

另一个场景先安装 `endurance`，再两次施加 `fortify`，演示常驻与状态属性修饰、
叠层、到期和卸载。

投射物投递数据可显式配置命中行为和施法移动策略：

```yaml
data:
  speed: 100
  explosionTrigger: OnHit
  gate:
    windupSeconds: 0.2
    casting:
      canMove: true
      canInterrupt: true
      interruptOnDamage: true
```

`explosionTrigger` 支持 `None`、`OnHit` 和 `OnArrival`，只决定运行时投射物
是否产生爆炸事实，不替代下游的 `ProjectileHit` 或 `ProjectileExplosion`
效果触发器。`gate.casting` 可省略；声明时必须同时给出移动与中断权限，
施法时长使用 `windupSeconds`；若还显式声明 `durationSeconds`，两者必须相等。
通道时长与中断返还是独立的显式字段。

有限充能也可配置事件触发的恢复：

```yaml
gate:
  charges:
    maximum: 3
    initial: 0
    recovery: { intervalSeconds: 10, mode: Parallel }
    conditionalRecovery:
      - amount: 1
        trigger: { type: Signal, signalId: recharge }
```

每条恢复触发器使用正常的来源范围、条件和概率，定向绑定的能力实例，但不激活能力。
仅在触发器和能力已注册且获授权时观察；入队后注册或授权改变则拒绝旧恢复。
恢复截断到上限；串行计时保留最早的未完成计时，并行计时取消最晚的计时。

### 真实能力来源

`AbilitySourceInstance` 持有原能力对象及其绑定的运行部分；
`Unit.Abilities.Sources` 索引当前授权给单位的来源。只有预置安装器的目录工厂
在 `AbilitySourcesChangeRequestEvent` 内创建实例，支持先创建无持有者来源。
来源只能进入一个 Context，之后不能换到其他 Context；无能力的空来源合法。

一次请求可以同时分配、卸下、交换多个来源，并按运行 ID 原位更新能力等级。
来源归属、获选注册、触发路由、等级与属性投影在同一提交点改变。
工厂或投影失败时不发布成功且保留旧状态；提交后发布失败通过包含操作 ID 的
`CommittedOperationException` 明确报告，其余已提交计时及通知仍会调度。

`allowMultiple: true` 时每个成员独立保留原对象和贡献，同一来源可含多个同定义成员。
否则同单位按实例实际等级选最高；同级保留当前实例，其次按稳定的来源及成员登记顺序。
获选能力的门控失败不回退候补。原位升级保留运行身份、充能与冷却；
已创建来源的等级只能通过来源事件更改，共存策略不可更改。

只有获选注册接受新的激活和条件回充；候补及无持有者实例继续按模拟时间计时回充。
仅获选切换不取消已受理施法，实际卸下或转移后，释放仍检查原持有者授权。
新持有者激活使用独立预约身份，不覆盖旧施法；转移的是原对象与门控，不是状态副本。

以上是可信模拟内部操作，不是网络授权。`Holder` 表示激活授权，不是库存物理所有权；
库存、装备、交易同意及天赋树授权属于独立的 Loadout 职责。受管来源不能通过定义 ID
装卸或裸原子处理器变更绕过登记，应使用来源事件。

### 单位升级

`UnitLevelUpRequestEvent`、已提交的 `UnitLevelUpEvent` 及
`UnitLevelUpResultEvent` 由 Combat 持有，不依赖 Loadout。运行时请求声明预期
旧等级及明确的新等级；过时、不增长或超范围请求返回类型化拒绝，不发布升级。
受保护的预置处理器统一提交等级、公式投影及留存值；公式失败不发布成功，
提交后发布者故障保留升级并继续调度结果。通知保留请求优先级，避免后续奖励链
丢失排序语义。

请求可指定同一 Context 中已登记的 `CompletionReceiver`，仅将成功／拒绝结果返回
协调者；实际升级事实仍只在目标单位发布一次。不指定时结果仍返回目标单位。
跨 Context 或未登记的完成接收器在变更前明确拒绝。连续相对升级应等待结果后再读取
预期等级；其他来源先提交导致的 `StaleLevel` 需要调用方明确重新决策，不能冒充成功。

`Unit.MaximumLevel` 在构造时配置，默认 10，不再是全局静态上限。
角色等级与能力等级独立。现有 `SetLevel`／`LevelUp` 是低层状态建立辅助方法，
不等同于运行时成长事件流。`UnitLevelUp` 已通过统一触发路由观察提交后的升级，
提供原／新／差值类型化字段，保留来源过滤及优先级；record 副本不能重新发布。
可选 Loadout 的天赋成长规则可观察这些记录，不引入 Combat 对 Loadout 的反向依赖。

### 部分 Loadout 库存能力

可选 `Miaosoft.Sim.Loadout` 运行时已接入物品定义／实例、绑定所有者的库存，以及
获得、装备、卸下和丢弃请求；普通无效果物品也有合法的空来源。
背包容量只计未装备物品，总携带容量包含装备；替换按最终布局的净容量校验，
不以临时先卸后装的数量拒绝合法操作。丢弃销毁实例，不能重新获得。

运行时创建通过 `ItemCreateRequestEvent`，而不是公开一个尚未建立能力的构造器。
只有预置安装器创建原能力并提交物品的 Context 绑定后才发布 `ItemCreatedEvent`。
创建不授予库存所有权或激活授权，也不重新采样无关协调者的属性投影；
获取及装备复用这些原对象。工厂失败不暴露半成品；即使普通无能力物品从未被
获取，创建后也不能进入另一个 Context。

库存策略通过 `IAbilitySourceChangePreparation` 在来源执行期间准备物理所有权、
物品修订及槽位变更，由预置安装器唯一创建／挂载能力并在同一事务提交全部状态。
不能在较早的排队处理器里提前准备库存事务，否则单执行者也可能在同一时间戳
形成过时计划。准备策略只能暂存领域状态，不自行提交、发布或创建处理器。
容量和显式版本不匹配返回类型化拒绝；工厂或投影异常不改变库存和能力。
另一项提交后通知失败时，库存完成通知仍会调度。

`TradeSession` 已接入精确物品 ID 和显式目标槽位的双边版本化同意。
任一条款更新撤销双方同意；库存变化使同意失效但不锁定库存。
最终来源准备重新检查共享策略及双方库存修订，校验双方最终净布局，
将物理所有权、原能力来源与同意关闭在同一事务提交。双方已装备且携带满额时
仍可交换原物品及原门控；单向赠予不创建虚构对换物品。投影失败不移动物品，
同意会话保持开放；已完成会话不能重复交换。应用须显式提供共享资格策略，
该策略在真正提交时复检，不只在确认时检查。
双方还必须在最终准备时保有各自唯一的当前库存及匹配交易功能；
已脱离的陈旧功能在所有权变更前明确失败，不能继续修改旧库存对象。

`Miaosoft.Sim.Loadout.Content` 的 `AddInventoryContent` 注册库存创作模块，
加载时还须显式选择 `LoadoutContentServiceCollectionExtensions.InventoryModuleId`，
不会隐式启用运行时功能。`item` 集合跨文档统一编译；引用的能力可以在后续文件
声明，编译结束再校验引用。目录只保留不可变定义，不创建运行能力。
未知字段、重复 ID、缺失引用或无效能力等级均报告来源文件。无能力的普通物品
不需要能力内容模块。与所引用能力定义一起加载的物品文档可以包含：

```yaml
schemaVersion: 1
item:
  - id: tool
    name: Tool
    slotType: weapon
    abilities: [{ id: endurance, level: 3 }]
```

通过 `catalog.GetSection<ItemCatalog>("item").Require("tool")` 取得定义，
再用前述创建事件生成新的运行时物品。

### 角色天赋与装备授权

用 `AddTalentContent` 注册并独立选择 `loadout-talents`，不隐式启用库存。
不可变 `TalentTreeCatalog` 跨文件编译 `talentTree`，验证节点依赖、等级、互斥及
能力引用，不创建运行时处理器。只有点数的空树不需要能力内容模块。
运行时先将单位绑定到 Context，再用所选目录定义构造 `UnitTalents(unit, definitions)`，
注册其 `TalentDirectoryHandler`。这一步为角色准备各树及其独立保留的领域观察者，
不会创建能力实例；树在首次授权提交后公开。仍只有预置安装器挂载观察者及创建能力处理器。

各树独立持有点池、节点等级、原节点来源和成长进度。
`TalentAuthorizationRequestEvent` 用真实 `IAbilitySource` 授予／撤销授权，不能以
虚构计数或物品定义 ID 代替实例；重复授权不增加数量。失去最后授权时移除该树的
观察者、成长订阅和新能力激活权限，保留原对象与进度。重新授权恢复同一棵树、
观察者及原能力；固定角色的来源不能转给另一单位。排队操作带授权代次，并在实际准备时
复检。本次不实现洗点。

`TalentNodeAllocateRequestEvent` 在检查最大节点等级、显式节点／分支／全树点数门槛及
双向互斥后消耗一点；`tier` 仅用于展示，不隐含“每层五点”。
只有显式开启 `growAbilitiesWithRank` 时才原位提升能力等级，值为初始能力等级加已购
节点等级减一；否则保留配置等级。节点、点数、来源竞争、能力等级和属性投影同事务提交。

`TalentGrowth` 是复用统一触发路由的真实观察能力，其 `data` 声明树和规则；对应树规则
反向关联该能力。加载时双向验证，但不创建处理器。
规则每累计 `progressPerReward` 份有效进度奖励 `pointsPerReward` 点，并保留余数；
`maximumRewards: 1` 表示一次性奖励，省略则不限奖励次数。未授权期间不累计、不补放。
例如同时加载能力／伤害类型模块及所选天赋模块：

```yaml
talentTree:
  - id: training
    name: Training
    growthRules:
      - { id: practice, abilityId: training.progress, progressPerReward: 5 }
ability:
  - id: training.progress
    type: TalentGrowth
    allowMultiple: false
    trigger: { type: Signal, signalId: practice }
    data: { treeId: training, ruleId: practice }
```

十一次匹配信号奖励两点、余一次，计数归角色树而非触发器。
也可以用条件筛选已提交的 `UnitLevelUp` 并读取类型化等级差值。

`TalentGrowth.data.progress` 可选，以当前触发类型的类型化 schema 编译数值表达式，
默认 `"1"`。结果须为不超过 `int.MaxValue` 的正整数；空值、小数、非正数、
非有限值及超界值均明确报错，不取整、不静默兜底。一次观察可以贡献多份进度，
完成多个阈值并保留余数；`maximumRewards` 仍限制奖励发生次数。
点数存储溢出拒绝整次进度更新。例如每**实际升一级**奖励一点的规则可使用：

```yaml
trigger: { type: UnitLevelUp }
data: { treeId: training, ruleId: practice, progress: "event.level.delta" }
```

阈值一、初始三点时，1→4→10 分别奖励三点和六点，最终十二点。
直接以四级创建角色不重建此前三次奖励，未授权期间升级也不补放。
进度表达式失败不会回滚已经提交的上游事件。

装备桥接须第三次显式选择：注册 `AddEquipmentTalentContent`，在库存和天赋之外加载
`loadout-equipment-talents`。`equipmentTalent: [{ itemId: tool, treeIds: [training] }]`
关联已有定义，只有背包物理所有权不会授予树。将
`EquipmentTalentCatalog.CreatePreparation(talents)` 传给 `UnitInventory` 构造器才启用
运行时桥接；仅加载内容不会启用功能。

桥接与库存操作、双边交易共享提交点，按树计算**净授权**；同树授权的装备替换不会
无故卸下／重装观察者或推进授权代次。两件已装备的原物品分别计数，撤销其中一件不影响
另一授权。交易只移动原物品及其授权，不移动赠予者的角色树、原节点能力、已用点数或
成长余数；接收者使用自己的树。属性投影失败时，库存、树授权、观察者及效果整体回滚。

以上是可信进程内能力，而非分离的网络快照。私人集成仍待完成；
Core、Combat 及基础示例不引用 Loadout。

### 死亡授权领取

在遗体所属库存上显式组合 `DeathTradeDirectory`，注入
`IDeathTradeDistributionPolicy` 和实时交互谓词，再注册 `DeathTradeHandler`。
分配策略从已确认死亡中选择领取者及精确物品实例 ID，框架不硬编码击杀者、
阵营或范围公式。交互谓词提供模式的几何、资格及**双方各自**的适用范围，
包括遗体使用基础范围的具体规则。

单位的 `CurrentDeathId` 在存活时为空，实际死亡提交时绑定尝试 ID，
早于 `DeathResolvedEvent` 的发布。授权保留死亡身份、领取者及当次实际死亡表示。
复活并再次死亡不能恢复旧授权，即使新一轮已确认死亡观察尚未运行。

领取者当前库存／交易功能处理 `DeathTradeInspectRequestEvent`；
只有仍在场且交互范围有效的检查才返回获授权物品视图。
`DeathTradeClaimRequestEvent` 绑定检查修订，并在实际准备时重新验证双方库存修订。
这是指定目标槽位的单向领取，不伪造死者的交易同意。
非授权实例、过期检查、库存变化、容量或策略拒绝均返回类型化结果。
即使注入的谓词始终为真，框架也独立拒绝已从地图移除的那具实际遗体。

领取沿用普通交换的原实例转移路径，并在同一提交点组合装备／天赋授权桥接。
投影失败保留双方库存、来源及检查状态，允许合法重试；
成功会使已用检查失效，但保留剩余物品及授权，以便再次检查领取。
显式 `DeathTradeCleanupRequestEvent` 只销毁剩余物品并关闭访问；
移除遗体不会悄悄销毁保留库存，遗体放置／移除由模式负责。
这些是可信服务端运行时能力：网络入口须绑定真实调用者并投影分离视图，
不能从客户端接收进程内授权对象。

### 状态施加及周期效果

```yaml
- id: fortify
  type: Status
  allowMultiple: false
  trigger: { type: ProjectileHit, sourceAbilityIds: [bolt] }
  data:
    durationSeconds: 5
    stackPolicy: StackOnly
    stackLimit: 2
    modifiers:
      - { target: lifePointMax, formula: "ability.level * 10 + target.level" }
```

施加后的实例保留释放时的能力等级。实时租约必须使用 `Independent` 且时长为正；
`protectedUntilExpiry` 仅适用于这类租约。数值兜底须显式配置，仅生产求值策略
可使用并报告诊断。

状态的 `data.periodicEffects` 编译为标准伤害/治疗模板，不创建额外能力或触发器：

```yaml
periodicEffects:
  - type: Damage
    tick: { tickCount: 3, tickIntervalSeconds: 1, firstTickDelaySeconds: 1 }
    damage: [{ type: impact, formula: "ability.level * 2" }]
  - type: Heal
    tick: { tickCount: 3, tickIntervalSeconds: 1, firstTickDelaySeconds: 1 }
    amount: 2
```

伤害类型须在选中的目录中声明。伤害分量与普通伤害共用 `type`、`formula`、
可选的 `fallback` 和 `priority`。治疗使用非负整数 `amount` 或
`restoreToMaximum: true`，不可同时配置。每项必须有正数 `tickCount` 和
`tickIntervalSeconds`，非负的首 tick 延迟默认零；立即发生的首 tick
继承创建连锁预算。后续 tick 使用模拟时间，包括具有独立实时租约的状态。
伤害和治疗各自排队，不保证同一时刻先伤害后治疗。

效果固定释放时的能力等级与来源快照，每次伤害结算读取当时的单位属性。
规则显式施加使用请求等级（默认 1），保留真实规则来源，施为角色与产生效果的
能力可以为空，不能用受者冒充施为者。有限状态时长也约束显式施加，除非请求提供了
自己的模拟到期时间。卸载或到期会取消剩余的所属 tick。安装请求不能用另一份
周期列表覆盖内容中的已编译效果。

同一来源与原施为者分组内，同级重施保留原周期器和 tick 时间。
`StackOnly` 与 `ExtendAndStack` 在上限内增加层数，`ExtendOnly` 不增加层数。
编译的周期效果创建结算时读取当前层数；伤害按既有分量精度量化每层结果，
再累加各层后减伤，固定治疗量乘以层数，回满治疗仍受生命上限约束。
延长租约不会重启已经结束的 tick。高等级替换取消旧周期器，新状态从一层开始；
`Independent` 每次施加仍创建新状态及独立周期。
[内容测试](../../tests/Content/DamageCatalogTest.cs)验证伤害和治疗、读条中升级、
目标实时属性、无角色规则、到期、配额拒绝的原子性，以及同级叠层／延长不新增周期器。

`Death` 是显式安装的常驻能力（`trigger: { type: None }`，省略 data 或使用 `data: {}`），
处理已有的待确认死亡／提交事件链并发布一次确认死亡，不是命令或人为补发的死亡通知。
未知 data 字段及主动触发器在加载时拒绝。框架不会为所有单位隐式安装死亡处理；
网络 Loadout 用例为 NPC 显式安装，再用真实伤害驱动死亡。

`BaseProperty` 是使用 `None` 触发器的独立常驻能力，与已施加状态共用加法修饰
schema 和优先级顺序，但它本身不是状态。修改其等级不必替换运行实例。当前单位
属性投影变化时两类能力一起重算。以下重算策略作用于能力内的全部修饰项，
不提供逐公式的任意事件订阅。

```yaml
- id: endurance
  type: BaseProperty
  allowMultiple: false
  trigger: { type: None }
  data:
    modifiers:
      - { target: lifePointMax, formula: "ability.level * 5" }
```

触发器的来源范围与条件过滤的是**原事件**；响应由自己的持有者执行并使用自己的
激活门控。能力的可选 selector 决定效果目标。治疗请求与确认的生命增加是不同观察；
已释放效果保留包括释放时能力等级在内的快照，而单位运行属性可在实际结算时读取。
读条中的升级在释放时生效，但不改写之前的效果。
状态监听器可使用运行时门控而不因此成为可命令施放的能力；施加后的租约从释放时
开始。释放时过滤已死亡或退役的目标，全部失效时失败且不消耗门控。已受理的施法
在监听器移除后仍可完成，但原持有者必须仍有授权；已释放状态的
来源凭证绑定原请求和安装事件，不能重放。状态 YAML 接受目录支持的触发事件；
`None` 声明供真实来源显式施加的效果，不注册监听器。命令找不到可命令能力或所选
能力未获授权时，在向观察者发布前即失败。状态支持加法表达式修饰、
叠层、租约、门控和周期内容；可选投影重算策略见[属性公式参考](trigger-conditions.md)，
不代表任意事件订阅。私人集成仍未完成。
带门控的状态将所有选中目标的安装、周期效果、门控和能量消耗放在
同一事务中准备；配额拒绝不会改变门控或任何目标。提交后的发布故障明确报告为已提交，
其余已提交事件仍会被调度。

定义 ID 标识可复用内容，运行实例 ID 选择已安装能力，激活/命令 ID 标识一次操作；
它们不能代替所有权与授权校验。同定义存在多份已安装命令能力时必须显式选择。

## 内容扩展与表达式

示例通过依赖注入注册内容模块、为单个目录选择模块，并为 `ProjectileHit` 条件
注册 `target.life` 字段；该字段仅由示例提供。字段注册按事件类型隔离，在使用定义前
封存。支持事件、内建字段、空值、运算符及可执行示例见
[当前完整表达式速查](trigger-conditions.md)。无效内容在加载时明确拒绝。

## 验证与后续边界

按[根目录概览](../../README.zh-CN.md#运行与验证)中的命令串行运行，并查看每项测试
输出。前两个示例使用测试运行器，不是交互式客户端；第三个示例通过共享 Client SDK
验证真实回环网络，唯一交互客户端为 Console。详见[网络契约及限制](networking.md)。
剩余实现、私人迁移和权利检查完成前，项目不能
宣称已具备公开发布条件。
