# Tag 系统

## 架构

Tag 为属性请求提供用户定义的语义信息。Tag 定义存放在 World 级只读 `TagCatalogBlob`；运行时请求和事件使用单位实体上的扁平 DynamicBuffer 保存 Tag。

Tag 缓冲没有业务数量上限。`InternalBufferCapacity` 只决定实体 Chunk 内的初始容量，超过后由 DynamicBuffer 使用原生内存扩容；清空长度后容量会保留并复用，不产生托管 GC。查询按单位当前 Tag 总量线性扫描。

## TagId

`TagId` 是 64 位标识：

```csharp
TagId attack = TagId.Combine(authorId, localId);
```

- 高 32 位：作者 ID。
- 低 32 位：作者内部 ID。
- `ToString()` 输出 `作者ID:本地ID`。

作者 ID 0 的本地 ID 0–99 全部由库保留，外部配置不得创建：

- `LibraryTags.None`：`0:0`，不是有效业务 Tag，只表示无父节点或无效值。
- `LibraryTags.Recalculation`：`0:1`，由库自动附加到修改器重算事件。
- `0:2`–`0:99`：预留。

作者 0 从本地 ID 100 开始可用于开发者 Tag。其他作者 ID 不受该保留区间影响。

## TagCatalog

`TagDefinition` 由 `Id` 和 `ParentId` 组成。`ParentId = LibraryTags.None` 表示根节点。

```csharp
TagId attack = TagId.Combine(1, 1);
TagId miss = TagId.Combine(1, 2);

BlobAssetReference<TagCatalogBlob> catalog = TagCatalog.Build(
    new[]
    {
        new TagDefinition
        {
            Id = attack,
            ParentId = LibraryTags.None,
        },
        new TagDefinition
        {
            Id = miss,
            ParentId = attack,
        },
    },
    Allocator.Persistent);

AttributeAccess.CreateTagCatalogSingleton(entityManager, catalog);
```

构建时会：

- 拒绝 `None` 和全部库保留 ID 的外部定义。
- 自动注入 `LibraryTags.Recalculation`。
- 校验父节点存在。
- 拒绝父链循环和冲突定义。
- 排序并冻结为 BlobAsset。

目录由调用方持有并负责 `Dispose()`。可通过 `TagCatalog.TryFindDefinition()` 查询定义，或通过 `AttributeAccess.TryGetTagCatalog()` 获取 World 单例。

## 层级匹配

Tag 层级使用显式父节点表达。若 `Miss` 的父节点是 `Attack`：

- `Contains(..., Miss)`：true。
- `Contains(..., Attack)`：false，只做精确匹配。
- `Matches(..., Attack, catalog)`：true，可由自身或任意子孙命中。

父链查询最大深度为 `TagCatalog.MaxHierarchyDepth`。目录构建阶段已阻止循环。

## 请求 Tag

每条 `AttributeRequestTag` 包含：

- `RequestId`：所属单位内的稳定请求 ID。
- `Set`：`Reason` 或 `Cancel`。
- `Tag`：业务 TagId。

Reason 描述请求来源、性质和监听结果。Cancel 是多监听者协商集合：监听者可以增加、读取或删除取消原因；监听窗口结束时 Cancel 集合非空，请求结果为 `RequestCancelled`。

```csharp
ulong requestId = request.RequestId;

AttributeRequestTags.TryAdd(
    tags,
    requestId,
    AttributeRequestTagSet.Cancel,
    miss);

if (AttributeRequestTags.Contains(
        tags,
        requestId,
        AttributeRequestTagSet.Cancel,
        miss))
{
    AttributeRequestTags.Remove(
        tags,
        requestId,
        AttributeRequestTagSet.Cancel,
        miss);
    AttributeRequestTags.TryAdd(
        tags,
        requestId,
        AttributeRequestTagSet.Reason,
        noMiss);
}
```

公开查询与修改 API：

```csharp
bool exact = AttributeRequestTags.Contains(tags, requestId, set, tag);
bool empty = AttributeRequestTags.IsEmpty(tags, requestId, set);
bool hierarchical = AttributeRequestTags.Matches(
    tags,
    requestId,
    set,
    tag,
    catalog);
bool present = AttributeRequestTags.TryAdd(tags, requestId, set, tag);
bool removed = AttributeRequestTags.Remove(tags, requestId, set, tag);
```

`RequestId == 0` 或 `tag == LibraryTags.None` 不能加入集合。重复加入同一请求、同一集合、同一 Tag 不会增加重复项。

提交请求时可直接附带 Reason：

```csharp
AttributeAccess.TryRequestCurrentValueChange(
    entityManager,
    unit,
    health,
    deltaBits: -10,
    tag: attack);
```

多个初始 Reason 使用 `NativeArray<TagId>` 重载。提交 API 会校验这些 Tag 已存在于 World 的 TagCatalog。

监听阶段直接操作 DynamicBuffer 时，系统暂不强制校验新写入的 Tag 是否存在于目录；业务监听器应只写入已注册 Tag。

## 事件 Tag

每条 `AttributeResolvedEventTag` 包含：

- `EventId`：所属单位内的稳定事件 ID。
- `Set`：来源请求的 Reason 或 Cancel 集合。
- `Tag`：最终 TagId。

请求结算时会把监听结束后的 Reason 和 Cancel 两个集合都复制到结果事件。修改器重算事件没有 RequestId，并自动获得 Reason 集合中的 `LibraryTags.Recalculation`。

```csharp
bool hasReason = AttributeRequestTags.EventContains(
    eventTags,
    resolved.EventId,
    AttributeRequestTagSet.Reason,
    attack);

bool cancelledByMiss = AttributeRequestTags.EventContains(
    eventTags,
    resolved.EventId,
    AttributeRequestTagSet.Cancel,
    miss);
```

## 稳定关联

请求 Tag 通过 RequestId 关联，事件 Tag 通过 EventId 关联，不依赖 DynamicBuffer 下标。监听器可以删除或调整请求顺序，消费者也可以调整事件顺序，Tag 都不会绑定到错误记录。

RequestId 和 EventId 均为每个单位独立的 `ulong` 序列：

- 两个序列互相独立。
- `0` 无效。
- 初始分配值为 `1`。
- `ulong.MaxValue` 的下一项回绕为 `1`。
- Pending 转 Active、跨批次清空缓冲都不会改变已分配 ID。

跨单位保存关联时必须使用“单位稳定 ID + RequestId/EventId”。ID 只保证单位当前运行期内关联稳定，不是永久全局唯一标识。
