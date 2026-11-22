# 触发条件

[English](../en/trigger-conditions.md) · [架构](architecture.md)

可运行代码见 [combat.yaml](../../samples/LocalCombat/combat.yaml) 和
[LocalCombatSample.cs](../../samples/LocalCombat/LocalCombatSample.cs)。

```yaml
trigger:
  type: ProjectileHit
  sourceAbilityIds: [bolt]
  eventSourceScope: Self
  conditions:
    - { type: Expression, expression: "event.source == holder && target.life > 0" }
    - { type: Distance, anchor: HitPoint, maximum: 10 }
```

`target.life` 由示例模块注册，**不是内建字段**。条件过滤原事件，能力自身的
可选 selector 决定效果目标。顺序为来源范围→条件→概率→能力激活门控；
触发通过不代表施放成功。

## 支持的事件与预置条件

目前支持 `Command`、`ProjectileHit`、`ProjectileExplosion`、
`DamageSkillHit`、`BeingDamaged`、`TargetLifeReduce`、`Heal`、`Death`（`Pending` 或默认的 `Confirmed`）、
`Signal`、`ScheduledDamageTick`、`ScheduledHealTick` 和 `UnitLevelUp`。
其他事件路由尚在迁移；`None` 不可声明条件。
`Heal` 观察治疗请求而非实际生命增加；取消请求不会产生恢复确认，
请求治疗 10 点也可能实际只恢复 5 点。
`Signal` 必须配置非空白 `signalId`；其他触发类型拒绝此选项。信号包括延迟信号
均继承因果链预算；可以没有角色来源或产生能力，此时字段保持空值，不以受者代替。
周期监听器每个真实 tick 只观察一次，与受保护的伤害/治疗结算分离。
监听者的独立响应以自身持有者执行并使用自身门控，不冒充原施为者或周期结算器。
tick 条件中的能力身份来自原效果释放时的快照。
两种伤害触发器在真实发布时各至多观察原请求一次，不因接收器投递而重复；
来源和目标分别是原施为者与受害者，监听者不必是其中之一。
需要持有者是来源或目标时，应显式配置条件。
效果选目标配置在 `data.selector`，不放在 `trigger`。追踪选择器的
`origin: EventSource` 选择原事件施为者，`origin: EventTarget` 选择原事件目标，
`origin: Source` 选择响应能力的持有者。角色缺失或处于其他地图时选择失败，
不消耗能力门控；其他选择器形状暂不支持这两种事件角色来源。

```yaml
data:
  selector: { type: tracking, origin: EventSource }
```

例如，投射物响应能力可以使用以下触发配置：

```yaml
trigger:
  type: Signal
  signalId: pulse
  eventSourceScope: Any
  conditions:
    - { type: Expression, expression: "event.source == null && event.target != holder" }
```

```yaml
trigger:
  type: ScheduledDamageTick
  sourceAbilityIds: [payload]
  eventSourceScope: Any
  conditions:
    - { type: Expression, expression: 'event.ability.definition == "payload" && event.source != holder' }
```

完整可执行的信号与周期响应场景见
[DamageCatalogTest.cs](../../tests/Content/DamageCatalogTest.cs)。

- `eventSourceScope`：默认 `Self`；`Ally` 包含自身，`Enemy` 要求不同阵营，
  `Any` 也包含空施为者。条件数组不会覆盖来源范围。
- `TargetRelation`：`relation: Self|Ally|Enemy|Any`，缺失目标均不匹配。
- `Distance`：`anchor: Source|Target|HitPoint`，`maximum` 为有限非负数。
  使用同一地图实例的距离规则及持有者在该地图的表示；缺少空间数据不匹配。
  `HitPoint` 仅适用于投射物命中/爆炸，使用事件记录的命中地图，不使用施为者
  当前地图代替它。
- `Expression`：`expression` 须返回布尔值。预置条件与表达式按声明顺序共同
  做 AND；不兼容的参数或无效枚举在加载时失败。

## 内建字段

以下字段可用于上述支持事件；事件缺失的数据保持空值，不虚构来源。
角色、能力、授予来源的身份是不同语义的不透明类型，不是字符串或授权凭据。

| 字段 | 类型 | 可空 |
|---|---|---|
| `holder` | 角色身份 | 否 |
| `event.source` | 角色身份 | 是 |
| `event.target` | 角色身份 | 是 |
| `event.ability` | 能力身份 | 是 |
| `event.ability.definition` | 字符串 | 是 |
| `event.ability.source` | 授予来源身份 | 是 |
| `event.ability.source.category` | 字符串 | 是 |
| `listener.ability` | 能力身份 | 否 |
| `listener.ability.definition` | 字符串 | 否 |
| `listener.ability.source` | 授予来源身份 | 是 |
| `listener.ability.source.category` | 字符串 | 是 |

`event.ability` 是直接产生效果的能力，不是连锁最初的能力。`Command` 中被请求
的能力不等于产生事件的能力，因此其 `event.ability*` 字段均为空。
死亡事件来源为可空击杀者、目标为死者；爆炸没有单位目标。已释放效果保留
能力及来源快照。监听字段查询实际绑定的能力；缺失或不一致的绑定明确报错，
不能假装为正常的空授予来源。

### 升级快照

`UnitLevelUp` 只观察一次已提交的正向升级，不观察请求或逐接收器投递。
原事件来源和目标都是实际升级的单位，不虚构产生能力；来源的自身／友方／敌方
过滤仍相对监听者计算。以下非空数值字段只存在于该触发类型的 schema：

| 字段 | 记录值 |
|---|---|
| `event.level.old` | 原等级 |
| `event.level.new` | 已提交的新等级 |
| `event.level.delta` | 新减旧，严格为正 |

这些是不可变事件值，不是实时单位属性查询。观察及定向激活保留请求优先级
和因果链。同一通知包括其 record 副本不能重新发布。可选 Loadout 的
`TalentGrowth` 能力可据此应用显式选择的树规则；Combat 本身不发放天赋点。

## 语法与类型

优先级从高到低：括号/函数；一元 `! + -`；`* /`；`+ -`；
`< <= > >=`；`== !=`；`&&`；`||`。

字面量包括有限数字（可用指数形式）、双引号字符串和小写保留字
`true`、`false`、`null`。字符串支持 `\"`、`\\`、`\n`、`\r`、`\t`。
不支持赋值、反射、任意方法调用或脚本；字段名称区分大小写。

| 类型 | 有效比较 |
|---|---|
| 有限双精度数 | `== != < <= > >=` |
| 布尔 | `== !=` |
| 字符串 | `== !=`，序数比较、区分大小写 |
| 身份 | 仅同语义身份类型之间的 `== !=` |

不进行隐式转换，支持显式 `== null` / `!= null`。
`1 == "1"`、`true > false`、`holder == event.ability`、
身份与 GUID 字符串比较均在加载时报错，即使位于将被短路的分支也不例外。

函数名不区分大小写：`min`/`max` 至少两个数字参数，`clamp` 三个参数，
`floor`、`ceil`/`ceiling`、`round`、`abs` 各一个参数。`if(condition, whenTrue, whenFalse)`
要求布尔条件及同类型的非空分支，只计算选中的分支；条件为未知时结果也为未知。
参数从左到右求值，`round` 使用中点取偶。

LocalCombat 实际执行的聚合示例：

```text
event.source == holder && event.target != holder
event.ability != listener.ability && event.ability.definition == "bolt"
event.ability.source == null && listener.ability.source == null
event.ability.source.category == null || listener.ability.source.category != "Equipment"
(2 + 3) * 4 / 2 - 1 == 9 && -2 < +1 && 2 >= 2 && 2 <= 3 && 3 > 2
min(3, 2, 1) == 1 && MAX(1, 3) == 3 && clamp(5, 0, 3) == 3
if(event.source == null, true, event.source == holder)
floor(1.9) == 1 && ceil(1.1) == 2 && ceiling(1.1) == 2 && round(2.5) == 2 && abs(-2) == 2
"Bolt" != "bolt" && (true == !false)
abs((0.1 + 0.2) - 0.3) < 0.000001
```

有限双精度仍存在浮点舍入；需要近似相等时应显式指定容差。

## 空值、顺序及错误

普通比较涉及缺失值时为未知，两个缺失来源也不相等。`!未知` 仍为未知；
只有最终为真才匹配。`false && ...` 和 `true || ...` 短路。
未知不短路：`未知 && false` 为假，`未知 || true` 为真。
条件数组也只有遇假才停止；未知之后已经执行的错误仍须传播。
“未知”是说明用语，不是可写字面量。

因此 `event.ability.source.category != "Equipment"` 排除空来源；
需要包含时显式使用 `== null || ...`。两者都不会放宽默认的角色来源范围。

未知字段、类型不匹配、错误函数签名在加载时报错。实际求值发生除零、
非有限数、无效 clamp 边界或字段错误时传播异常；条件表达式在生产环境
也**没有兜底**。数值公式共用类型内核，但使用独立的、显式配置并带诊断的
生产环境兜底策略。后续错误不回滚已提交效果。

## 模块扩展

实时单位生命／等级／阵营及一般事件数值不是内建字段；上面的已记录升级字段
是特定事件的例外。可信模块须在目录 schema 封存前，
按适用事件显式注册：

```csharp
registry.TriggerConditions().For(TriggerType.ProjectileHit)
    .RegisterNumber("target.life",
        value => value.EventTarget?.Property.LifePoint, nullable: true);
```

本示例的全部扩展为 `target.life`（可空数字，仅命中条件）和
`source.level`（数字，用于战斗数值公式）。
模块可注册数字、布尔、字符串或类型化身份；重复注册及封存后注册失败。
每个目录独立绑定；注册命中字段不会使其自动适用于命令条件。
读取器按实际判断时点读取状态，错误向上传播；表达式限制不构成可信模块代码的沙箱。

伤害/减伤数值公式使用独立 schema：伤害模块注册 `ability.level`、可空的
`source.level`、可空的 `target.level`，以及 `raw`（可空数字，减伤时提供）。
模式可按需注册自己的字段；例如 Adventure 注册 `target.armor.<type>`。
这些字段不会自动进入条件 schema；数值公式须返回数字，条件须返回布尔值。

常驻 `BaseProperty` 和状态属性公式另用独立 schema：`ability.level`、可空的 `source.level`、
`target.level`、可空的 `target.life`、`target.lifeMax`、`target.energy` 和
`target.energyMax` 均为数字。内建加法修改目标为 `lifePointMax`、`energyMax`、
`moveSpeedBase`、`moveSpeed`、`experienceMax`、`visionRange` 和 `stealth`，模块
可扩展。公式读取已施加状态的固定能力等级，以及重算时的来源与目标投影值。常驻能力使用
自身当前等级；自身作为来源时，来源和目标都读取正在计算的拥有者投影值。
`BaseProperty` 和 `Status` 的 data 均接受投影重算策略：

```yaml
recalculateOn: [AbilityLevelChanged, TargetLevelChanged]
modifiers:
  - { target: lifePointMax, formula: "ability.level * 10 + target.level" }
```

不声明等于 `[ProjectionChanged]`，保留已有投影生命周期（安装、移除、叠层、
单位等级变化等）执行时重新求值的行为。`AbilityLevelChanged` 与
`TargetLevelChanged` 分别在当前能力等级或受者等级不同于上次取值时重新求值，
两者可组合。`[]` 保留每次注册时各修饰项的首次值。首次注册、重新注册或原施为者／
受者身份变化始终重新求值，但不重建能力。这是投影策略，不是新的激活触发器，
也不是任意事件订阅；不会在另一个单位上隐式安装字段变化监听。

叠层将保留的每层值乘当前层数，不在策略之外重新采样公式；若新层数使保留值
超出目标可表示范围，则拒绝本次操作，不隐式重新求值或截断。
取值缓存、兜底标记、等级与最终投影均在同一事务提交；编译定义不保存运行实例缓存。
未知、数字、重复及 null 项均非法；仅首次取值写 `[]` 而不是 `[None]`，
`ProjectionChanged` 不与更窄的等级策略混用。

