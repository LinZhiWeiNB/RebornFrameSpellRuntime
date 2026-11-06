# Buff 系统

本文是 Buff 运行时合同。实现位于 `Runtime/Buff`，测试位于 `Tests/Runtime/Buff`。

Buff 流水线以 Unity Entities 的单位实体为运行时载体。Buff 定义放在只读 BlobAsset 中。单位保存实例、层、周期状态、Tag 贡献、请求和结算事件。一个 World 只能创建一个 `BuffPipeline`。流水线不自动加入 Unity 更新循环，由上层锁步在主线程、非重入地调用 `UpdateOnce(logicTick)`。核心生命周期系统保持 Burst 兼容。项目自定义效果首版在主线程分发，不进入 Burst。

各演算端使用同一份目录、同一份输入和同一套顺序。不使用预测、回滚、墙钟时间或浮点随机。`Entity` 下标和 ECS Chunk 顺序不参与语义。

## 标识

`BuffId`、`BuffEffectId`、`BuffSignalId`、`BuffIteratorId` 都是 64 位标识，组合方式和 `AttributeId` 相同：

```csharp
BuffId id = BuffId.Combine(authorId, localId);
```

作者 0 的本地编号 0–99 由库保留，外部配置不得占用。

单位实体带 `SpellStableId`。它是非零 `ulong`，在一个 World 的当前战斗内唯一且不复用。跨实体引用、网络和排序都使用它。

`BuffInstanceId` 与属性修改器现有字段一致，使用正 `long`。`0` 无效。序列耗尽时本次模拟失败，不回绕。实例 Id 只在创建请求完成稳定排序之后分配。

公共写入只允许在主线程、两次 `UpdateOnce()` 之间进行。每条请求在提交时取得 World 级 `RequestId`。`RequestId` 是正 `ulong`，`0` 无效，耗尽时失败且不回绕。Pending 转为 Active 后 Id 不变。

任何会改变结果的操作都使用完整排序键，至少包含：

- 流水线阶段
- 目标 `SpellStableId`
- `RequestId`
- `BuffInstanceId`
- 定义内的层、触发器或效果序号

效果和事件按这个键输出。查询顺序、Chunk 顺序、哈希枚举和并行追加顺序都不是排序键。目录、生成的效果分发器和 schema 各自带一致性哈希，开局前各演算端必须一致。

## 定义

`BuffCatalog.Build()` 校验、排序并冻结为 `BuffCatalogBlob`。BlobAsset 由调用方持有；所有引用它的 World 停止使用后再 `Dispose()`。

一条定义包含：

- `DurationPolicy`：`Instant`、`Duration`、`Infinite`。永久是独立策略，配置里的 `0` 不是永久。
- 身份键：`PerTarget`，或 `PerTargetAndSource`。
- 叠层上限。达到上限时 `Reject` 或 `RefreshDuration`。
- 时长：`Shared` 或 `PerLayer`。叠层时可以保持剩余步数，或刷新全部层。周期计数同样可以选择保持或重置。
- 互斥组 `TagId` 和优先级。同优先级时，`RequestId` 较大者胜出。
- 施加条件、免疫和授予 Tag。条件是 `All`、`Any` 或 `NotAny`，匹配使用 `TagCatalog` 的父子规则。`NotAny` 表示一个都不能命中。
- 创建效果、更新效果、结束效果。更新原因是加层、退层、刷新和剩余步数变化。结束原因是到期、驱散、脚本移除、互斥替换、单位销毁、来源消失。
- 周期触发器与外部信号触发器。二者分开配置，每条触发器拥有自己的效果列表。周期触发器绑定一个时钟并带间隔；信号触发器绑定 `BuffSignalId`，可以再带冷却。

`Duration` 必须带正整数步数和一个 `BuffClockKey`。`Instant` 与 `Infinite` 没有剩余步数。步数和间隔都有单批安全上限，超出的配置在建目录时失败。

## 运行时数据

一个实例表示一条 Buff。可叠层时，层是该实例下的子状态，不另建实例。变长状态拆成固定大小元素，用稳定 Id 关联，不使用 DynamicBuffer 下标：

- `BuffInstanceElement`：实例 Id、定义 Id、来源稳定 Id、层数、创建时的 `logicTick`。
- `BuffLayerStateElement`：实例 Id、`LayerId`、来源稳定 Id、时钟键、剩余步数。
- `BuffTriggerStateElement`：实例 Id、触发器序号、周期或冷却剩余步数。
- `GameplayTagContribution`：`TagId`、来源类型、来源 Id。
- `BuffOwnedAttributeTarget`：该实例登记过持久修改器的目标实体稳定 Id。

`GameplayTagContribution` 与属性请求上的 `AttributeRequestTag` 是两套缓冲。多个 Buff 授予同一个 Tag 时各留一条贡献。移除一个实例只删除它自己的贡献。单位固有 Tag 使用独立来源，不写成某个 Buff 的贡献。

单层到期产生退层更新。最后一层消失才产生一次结束。瞬时效果不创建实例，也不能叠层、周期触发、授予持久 Tag、登记持久修改器或配置结束效果。

属性修改器仍由 `(BuffInstanceId, ModifierLocalId)` 标识。持久修改器只能由持续或永久实例登记。实例结束时，框架按 `BuffOwnedAttributeTarget` 对每个目标提交 `RemoveByBuff`。结束效果不负责记住这件清理。

## 步进时钟

`UpdateOnce(logicTick)` 每逻辑 tick 调用一次。这次调用处理创建、销毁、信号和效果，本身不减少剩余步数。

剩余步数只响应 `BuffIterateRequest`。时钟是精确键：

```csharp
public readonly struct BuffClockKey
{
    public readonly BuffIteratorId IteratorId;
    public readonly ulong ScopeStableId;
}
```

`ScopeStableId == 0` 是一个明确的全局时钟，不是“所有单位”的通配。单位自身回合使用该单位的 `SpellStableId`。用来源回合计时的层，使用来源的稳定 Id。项目要推进多个单位时，按稳定 Id 排序后为每个时钟提交一条请求。

`Steps` 必须为正整数。`Steps = N` 与连续 N 次 `Steps = 1` 等价。每个虚拟步按这个顺序执行：

1. 结算到点的周期触发。
2. 扣除匹配层的剩余步数。
3. 处理到期并停止该实例的后续虚拟步。

没有匹配步进请求的 tick，剩余步数保持不变。本 tick 新建的实例和新建的层会参加本次 `UpdateOnce` 里位于创建阶段之后的信号和步进。

回合制每 tick 仍调用 `UpdateOnce()`。普通 tick 不提交回合时钟。某个单位回合结束的 tick 提交：

```csharp
BuffAccess.TryIterate(entityManager, new BuffClockKey(roundClock, unitStableId), steps: 1);
```

实时 Buff 监听帧时钟时，由项目在每个逻辑 tick 显式提交一步。`Time.deltaTime` 不进入流水线。

## 批次顺序

一次 `UpdateOnce(logicTick)` 按以下顺序执行：

1. 收集单位。缺失、重复或为零的稳定 Id 直接失败。
2. 收集并处理创建、叠层、刷新和互斥替换，然后分发这个阶段排出的效果。
3. 收集并处理当前 Pending 中的信号，然后分发这个阶段排出的效果。
4. 收集并处理步进，然后分发这个阶段排出的效果。
5. 再次收集并处理此前尚未收集的信号，然后分发效果。
6. 收集并处理销毁，然后分发这个阶段排出的效果。
7. 最后一次收集并处理此前尚未收集的信号，然后分发效果。
8. 为墓碑实例追加 `RemoveByBuff`，并删除它们的 Tag、触发器、层和实例行。
9. 执行事件消费者。
10. 清空本 tick 的活动信号字节、效果请求和事件缓冲。容量保留。

每次收集都把对应 Pending 抽空。已经收集的信号留在本轮处理列表里，不会进入下一次收集。效果提交的新请求进入 Pending；只有收集点还没执行的类型会在本 tick 被处理。创建只收集一次，所以效果里的新创建留到下一次 `UpdateOnce()`。最后一轮信号效果提交的信号和销毁同样留到下一次。效果方法不能调用 `UpdateOnce()`。

销毁阶段只记录墓碑，不在效果分发前提交修改器删除。同一批创建里先创建再被替换的实例，仍会按“创建效果、结束效果、替代者创建效果”的顺序进入该阶段的分发。清理放在全部分发之后，因此这些效果即使登记了修改器，最终也会被 `RemoveByBuff` 撤掉。

每个实例最多结束一次。重复销毁产生拒绝结果，不第二次执行结束效果。

## 请求与事件

创建请求包含 `BuffId`、目标稳定 Id、来源稳定 Id 和 `RequestId`。销毁请求包含精确实例 Id 和结束原因。首版驱散由项目选出实例 Id；框架不内置按强度、数量或优先级挑选驱散目标的规则。

步进请求包含 `BuffClockKey` 和 `Steps`。信号请求包含 `BuffSignalId`、来源稳定 Id、主体稳定 Id、`RequestId` 和一段固定 schema 载荷。

`BuffResolvedEvent` 只在事件消费窗口内有效，种类为：

- `CreateApplied`
- `CreateRejected`
- `Updated`
- `Triggered`
- `RemoveApplied`
- `RemoveRejected`

事件保存 `RequestId`、实例 Id、目标与来源、原因、变化前后的层数和剩余步数。触发事件同时保存信号请求 Id 和信号载荷位置。消费者只读。消费者提交的新请求进入 Pending。

## 信号与触发

框架不定义受击、治疗、死亡等业务枚举。项目注册 `BuffSignalId`，在对应业务发生时提交信号请求。

信号触发器可以带 Tag 条件和冷却。周期触发器只由匹配的时钟推进。效果请求原样带上信号的请求 Id、来源、主体和载荷，同 tick 的多个同类信号可以区分。

## 效果与编辑器

项目用特性声明效果方法和参数结构。结构体的内存布局、字段声明顺序、padding、布尔宽度和端序都不是存储协议。每个效果和每个字段都有稳定 Id、版本和类型。

```csharp
[BuffEffect(authorId: 1, localId: 1, version: 1)]
public static class DamageEffect
{
    public struct Params
    {
        [BuffEffectField(fieldId: 1)]
        public AttributeId Attribute;

        [BuffEffectField(fieldId: 2)]
        public long Delta;
    }

    public static void Apply(in BuffEffectContext context, in Params args)
    {
        context.TryRequestCurrentValue(args.Attribute, args.Delta);
    }
}
```

构建期生成两份产物：

- `GameBuffEffectDispatcher.g.cs`。它按 `BuffEffectId` 做静态 `switch`，用生成的解码代码调用 `Apply`。分发在主线程执行，不使用反射、委托注册或闭包。自定义效果方法首版不进入 Burst。
- `buff-effects.schema.json`。独立编辑器只读这份 manifest，不加载 Unity 或游戏程序集。内容包含效果 Id、版本、schema 哈希、字段 Id、线类型、默认值、范围、枚举项、引用目录和显示用本地化 Key。

编辑器保存 `EffectId + PayloadVersion + {FieldId: Value}`。Unity 烘焙阶段完成旧版本迁移和引用校验，再写成小端、固定宽度、无 padding 的载荷。运行时只接受与生成分发器哈希一致的目录。字段重排不改变 `FieldId`，因此不破坏已有作者数据。

`BuffEffectContext` 提供本次请求的只读快照：实例、来源、目标、层数、阶段、原因、信号载荷、属性值和 Gameplay Tag。它还可以：

- 提交属性修改器或资源当前值请求。
- 把创建、销毁和信号放进 Pending。收集点还没执行的请求会在本 tick 处理，已经过了收集点的留到下一次 `UpdateOnce()`。

上下文不暴露内部 Buffer。效果代码是受信任的确定性扩展：不读取墙钟，不使用非确定随机，不写静态演算状态，不在热路径分配。异常视为代码错误并中止模拟，流水线不回滚本 tick。

库内置效果只连接现有属性与 Tag：登记修改器、提交当前值请求、授予或移除 Tag。伤害、治疗和控制由项目按同一契约声明。

## 与属性流水线的顺序

本文是技能运行时的锁步合同。流水线按各演算端可复算的确定性设计。根目录不再保留「只由权威端演算并广播结果」的旧文档。

每个逻辑 tick 的调用顺序是合同：

1. `BuffPipeline.UpdateOnce(logicTick)`。
2. `AttributePipeline.UpdateOnce()`。
3. 单位 Disabled、Destroy 或回收。

Buff 效果提交的属性命令在同 tick 的属性批次结算。属性事件消费者若再提交 Buff 请求，该请求进入 Pending，下一 tick 处理。首版不在一个 tick 内循环两条流水线。

单位在属性批次结算前被禁用或回收时，已入队的修改器命令不会被处理。回收必须走上面的顺序，或调用同步清理接口清空该单位的 Buff 状态和尚未结算的命令。

## 验证

后续实现需要覆盖：

- 两个 World 的实体创建顺序和 Chunk 布局不同，输入相同时逐 tick 状态哈希一致。
- 没有步进请求时剩余步数不变。一个单位的时钟不改变其他单位。
- `Steps = N` 与 N 次单步的周期触发、扣步和中途到期结果一致。
- 本 tick 新建的实例和新层会参加本次更新里位于创建之后的信号和步进。已经收集过的信号不会被下一次信号收集重复处理。
- 加层、单层到期、退层和最后一层结束的事件数量符合定义。
- 两个实例授予同一 Tag，移除其中一个后另一个的 Tag 仍在。
- 同一 tick 创建 A 后被 B 替换，效果顺序固定，属性修改器最终不残留。
- 调换系统遍历顺序不改变效果和事件顺序。
- 字段重排后旧作者数据仍能烘焙。schema 哈希不一致时，烘焙或启动失败。
- 容量预热后，框架热路径不产生托管 GC。临时排序和请求存储复用原生容器或 DynamicBuffer 容量。
