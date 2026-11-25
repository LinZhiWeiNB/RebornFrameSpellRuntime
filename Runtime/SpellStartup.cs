using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     一场战斗的 Spell 启动器。多次加入的目录在 <see cref="Start" /> 时合并，
    ///     相同定义保留一份，同一 ID 的冲突定义会失败。
    /// </summary>
    /// <remarks>
    ///     <see cref="Start" /> 不接收 Blob。它冻结已加入的定义，创建 Tag、属性、Buff 和法术
    ///     目录单例，并装配三条流水线。Blob 由本对象持有，<see cref="Dispose" /> 时释放。
    ///     World 仍由调用方持有。
    /// </remarks>
    public sealed class SpellStartup : IDisposable
    {
        private readonly List<AttributeDefinition> _attributes = new();
        private readonly List<BuffDefinitionData> _buffs = new();
        private readonly List<UnitAttributePresetEntry> _layout = new();
        private readonly List<SpellDefinitionData> _spells = new();
        private readonly List<TagDefinition> _tags = new();
        private BlobAssetReference<AttributeCatalogBlob> _attributeCatalog;
        private BlobAssetReference<AttributeLayoutBlob> _attributeLayout;
        private AttributePipeline _attributePipeline;
        private BlobAssetReference<BuffCatalogBlob> _buffCatalog;
        private BuffPipeline _buffPipeline;
        private BlobAssetReference<SpellCatalogBlob> _spellCatalog;
        private SpellPipeline _spellPipeline;
        private bool _disposed;
        private bool _started;
        private BlobAssetReference<TagCatalogBlob> _tagCatalog;

        private World _world;

        /// <summary>
        ///     销毁两条流水线并释放本对象持有的目录。重复调用不会产生效果。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _spellPipeline?.Dispose();
            _buffPipeline?.Dispose();
            _attributePipeline?.Dispose();
            _spellPipeline = null;
            _buffPipeline = null;
            _attributePipeline = null;
            Dispose(ref _spellCatalog);
            Dispose(ref _buffCatalog);
            Dispose(ref _attributeLayout);
            Dispose(ref _attributeCatalog);
            Dispose(ref _tagCatalog);
        }

        /// <summary>
        ///     追加一批 Tag。可以多次调用，相同定义会在启动时合并。
        /// </summary>
        /// <param name="definitions">待加入的 Tag 定义。</param>
        /// <exception cref="ArgumentNullException">定义列表为 null。</exception>
        /// <exception cref="InvalidOperationException">已经启动或已经释放。</exception>
        public void AddTags(IReadOnlyList<TagDefinition> definitions)
        {
            ThrowIfClosed();
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));

            for (var index = 0; index < definitions.Count; index++) _tags.Add(definitions[index]);
        }

        /// <summary>
        ///     追加一批属性定义。可以多次调用，相同定义会在启动时合并。
        /// </summary>
        /// <param name="definitions">待加入的属性定义。</param>
        /// <exception cref="ArgumentNullException">定义列表为 null。</exception>
        /// <exception cref="InvalidOperationException">已经启动或已经释放。</exception>
        public void AddAttributes(IReadOnlyList<AttributeDefinition> definitions)
        {
            ThrowIfClosed();
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));

            for (var index = 0; index < definitions.Count; index++) _attributes.Add(definitions[index]);
        }

        /// <summary>
        ///     追加单位共享属性布局。可以多次调用，相同属性且默认值一致的项会合并。
        /// </summary>
        /// <param name="preset">单位预设。</param>
        /// <exception cref="ArgumentNullException">预设列表为 null。</exception>
        /// <exception cref="InvalidOperationException">已经启动或已经释放。</exception>
        public void AddAttributeLayout(IReadOnlyList<UnitAttributePresetEntry> preset)
        {
            ThrowIfClosed();
            if (preset == null) throw new ArgumentNullException(nameof(preset));

            for (var index = 0; index < preset.Count; index++) _layout.Add(preset[index]);
        }

        /// <summary>
        ///     追加一批 Buff 定义。可以多次调用，内容一致的定义会在启动时合并。
        /// </summary>
        /// <param name="definitions">待加入的 Buff 定义。</param>
        /// <exception cref="ArgumentNullException">定义列表为 null。</exception>
        /// <exception cref="ArgumentException">列表中含有 null 定义。</exception>
        /// <exception cref="InvalidOperationException">已经启动或已经释放。</exception>
        public void AddBuffs(IReadOnlyList<BuffDefinitionData> definitions)
        {
            ThrowIfClosed();
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));

            for (var index = 0; index < definitions.Count; index++)
            {
                if (definitions[index] == null) throw new ArgumentException("Buff 定义不能为 null。", nameof(definitions));

                _buffs.Add(definitions[index]);
            }
        }

        /// <summary>
        ///     追加一批法术定义。可以多次调用，内容一致的定义会在启动时合并。
        /// </summary>
        /// <param name="definitions">待加入的法术定义。</param>
        /// <exception cref="ArgumentNullException">定义列表为 null。</exception>
        /// <exception cref="ArgumentException">列表中含有 null 定义。</exception>
        /// <exception cref="InvalidOperationException">已经启动或已经释放。</exception>
        public void AddSpells(IReadOnlyList<SpellDefinitionData> definitions)
        {
            ThrowIfClosed();
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));

            for (var index = 0; index < definitions.Count; index++)
            {
                if (definitions[index] == null) throw new ArgumentException("法术定义不能为 null。", nameof(definitions));

                _spells.Add(definitions[index]);
            }
        }

        /// <summary>
        ///     合并一份法术模块数据。项目可以多次调用，把不同开发者的数据加进来。
        /// </summary>
        /// <param name="data">法术模块数据。</param>
        /// <exception cref="ArgumentNullException">数据为 null。</exception>
        /// <exception cref="ArgumentException">效果版本超出范围。</exception>
        /// <exception cref="InvalidOperationException">已经启动或已经释放。</exception>
        public void AddModuleData(SpellModuleData data)
        {
            ThrowIfClosed();
            if (data == null) throw new ArgumentNullException(nameof(data));

            var tags = SpellModuleDataImport.Tags(data);
            var attributes = SpellModuleDataImport.Attributes(data);
            var buffs = SpellModuleDataImport.Buffs(data);
            var spells = SpellModuleDataImport.Spells(data);
            AddTags(tags);
            AddAttributes(attributes);
            AddBuffs(buffs);
            AddSpells(spells);
        }

        /// <summary>
        ///     合并一份序列化后的法术模块数据。
        /// </summary>
        /// <param name="bytes"><see cref="SpellModuleData" /> 的 protobuf 字节。</param>
        /// <exception cref="ArgumentNullException">字节为 null。</exception>
        /// <exception cref="ArgumentException">效果版本超出范围。</exception>
        /// <exception cref="InvalidOperationException">已经启动或已经释放。</exception>
        public void AddModuleData(byte[] bytes)
        {
            ThrowIfClosed();
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));

            AddModuleData(SpellModuleData.Parser.ParseFrom(bytes));
        }

        /// <summary>
        ///     冻结已加入的目录，创建目录单例，并装配法术、属性和 Buff 流水线。
        /// </summary>
        /// <param name="world">承载本场战斗的有效 World。</param>
        /// <exception cref="ArgumentNullException">World 为 null。</exception>
        /// <exception cref="ArgumentException">World 已销毁，或目录定义冲突。</exception>
        /// <exception cref="InvalidOperationException">已经启动、已经释放，或 World 里已有对应目录。</exception>
        public void Start(World world)
        {
            ThrowIfClosed();
            if (_started) throw new InvalidOperationException("Spell 已经启动。");

            if (world == null) throw new ArgumentNullException(nameof(world));

            if (!world.IsCreated) throw new ArgumentException("World 已被销毁。", nameof(world));

            BlobAssetReference<TagCatalogBlob> tags = default;
            BlobAssetReference<AttributeCatalogBlob> attributes = default;
            BlobAssetReference<AttributeLayoutBlob> layout = default;
            BlobAssetReference<BuffCatalogBlob> buffs = default;
            BlobAssetReference<SpellCatalogBlob> spells = default;
            var tagEntity = Entity.Null;
            var attributeEntity = Entity.Null;
            var buffEntity = Entity.Null;
            var spellEntity = Entity.Null;
            AttributePipeline attributePipeline = null;
            BuffPipeline buffPipeline = null;
            SpellPipeline spellPipeline = null;
            try
            {
                tags = TagCatalog.Build(_tags, Allocator.Persistent);
                attributes = AttributeCatalog.Build(_attributes, Allocator.Persistent);
                layout = AttributeCatalog.BuildLayout(attributes, MergeLayout(), Allocator.Persistent);
                buffs = BuffCatalog.Build(_buffs, Allocator.Persistent, out var runtimeEffects);
                spells = SpellCatalog.Build(_spells, KnownBuffIds(), Allocator.Persistent);

                var entityManager = world.EntityManager;
                tagEntity = AttributeAccess.CreateTagCatalogSingleton(entityManager, tags);
                attributeEntity = AttributeAccess.CreateCatalogSingleton(entityManager, attributes);
                buffEntity = BuffAccess.CreateCatalogSingleton(entityManager, buffs, runtimeEffects);
                spellEntity = SpellAccess.CreateCatalogSingleton(entityManager, spells);
                attributePipeline = AttributePipeline.Create(world);
                buffPipeline = BuffPipeline.Create(world);
                spellPipeline = SpellPipeline.Create(world);
            }
            catch
            {
                spellPipeline?.Dispose();
                buffPipeline?.Dispose();
                attributePipeline?.Dispose();
                if (world.IsCreated)
                {
                    var entityManager = world.EntityManager;
                    DestroyIfExists(entityManager, spellEntity);
                    DestroyIfExists(entityManager, buffEntity);
                    DestroyIfExists(entityManager, attributeEntity);
                    DestroyIfExists(entityManager, tagEntity);
                }

                Dispose(ref spells);
                Dispose(ref buffs);
                Dispose(ref layout);
                Dispose(ref attributes);
                Dispose(ref tags);
                throw;
            }

            _world = world;
            _tagCatalog = tags;
            _attributeCatalog = attributes;
            _attributeLayout = layout;
            _buffCatalog = buffs;
            _spellCatalog = spells;
            _attributePipeline = attributePipeline;
            _buffPipeline = buffPipeline;
            _spellPipeline = spellPipeline;
            _started = true;
        }

        /// <summary>
        ///     推进一个逻辑 tick：先推进法术时间轴，再处理 Buff 的创建、信号和步进，然后结算属性，最后收尾 Buff。
        /// </summary>
        /// <param name="logicTick">当前逻辑 tick，必须大于等于 0。</param>
        /// <exception cref="InvalidOperationException">尚未启动或已经释放。</exception>
        public void Tick(int logicTick)
        {
            ThrowIfNotStarted();
            _spellPipeline.Update(logicTick);
            _buffPipeline.BeginUpdate(logicTick);
            _attributePipeline.UpdateOnce();
            _buffPipeline.CompleteAfterAttributes();
        }

        /// <summary>
        ///     用启动时冻结的共享布局初始化单位，并写入 Buff 稳定 ID。
        /// </summary>
        /// <param name="entity">待初始化的单位。</param>
        /// <param name="stableId">非零且当前战斗内唯一的稳定 ID。</param>
        /// <exception cref="InvalidOperationException">尚未启动或已经释放。</exception>
        public void InitializeUnit(Entity entity, ulong stableId)
        {
            InitializeUnit(entity, stableId, null);
        }

        /// <summary>
        ///     初始化单位，并覆盖资源的初始当前值。覆盖不会产生结算事件。
        /// </summary>
        /// <param name="entity">待初始化的单位。</param>
        /// <param name="stableId">非零且当前战斗内唯一的稳定 ID。</param>
        /// <param name="currentOverrides">资源初始当前值；null 表示不覆盖。</param>
        /// <exception cref="InvalidOperationException">尚未启动或已经释放。</exception>
        public void InitializeUnit(
            Entity entity,
            ulong stableId,
            IReadOnlyList<UnitResourceCurrentOverride> currentOverrides)
        {
            ThrowIfNotStarted();
            var entityManager = _world.EntityManager;
            AttributeAccess.InitializeUnit(entityManager, entity, _attributeLayout, currentOverrides);
            BuffAccess.InitializeUnit(entityManager, entity, stableId);
            SpellAccess.InitializeUnit(entityManager, entity);
        }

        private void ThrowIfClosed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SpellStartup));

            if (_started) throw new InvalidOperationException("Spell 已经启动，不能再加入目录。");
        }

        private void ThrowIfNotStarted()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SpellStartup));

            if (!_started) throw new InvalidOperationException("Spell 尚未启动。");
        }

        private List<BuffId> KnownBuffIds()
        {
            var ids = new List<BuffId>(_buffs.Count);
            for (var index = 0; index < _buffs.Count; index++) ids.Add(_buffs[index].Id);

            return ids;
        }

        private List<UnitAttributePresetEntry> MergeLayout()
        {
            var sorted = new List<UnitAttributePresetEntry>(_layout);
            sorted.Sort(PresetComparer.Instance);
            var unique = new List<UnitAttributePresetEntry>(sorted.Count);
            for (var index = 0; index < sorted.Count; index++)
            {
                var entry = sorted[index];
                if (unique.Count == 0 || unique[unique.Count - 1].Id != entry.Id)
                {
                    unique.Add(entry);
                    continue;
                }

                if (!unique[unique.Count - 1].DefaultValue.Equals(entry.DefaultValue))
                    throw new ArgumentException($"属性 {entry.Id} 的单位默认值冲突。");
            }

            return unique;
        }

        private static void DestroyIfExists(EntityManager entityManager, Entity entity)
        {
            if (entityManager.Exists(entity)) entityManager.DestroyEntity(entity);
        }

        private static void Dispose<T>(ref BlobAssetReference<T> blob)
            where T : unmanaged
        {
            if (blob.IsCreated)
            {
                blob.Dispose();
                blob = default;
            }
        }

        private sealed class PresetComparer : IComparer<UnitAttributePresetEntry>
        {
            internal static readonly PresetComparer Instance = new();

            public int Compare(UnitAttributePresetEntry left, UnitAttributePresetEntry right)
            {
                return left.Id.CompareTo(right.Id);
            }
        }
    }
}