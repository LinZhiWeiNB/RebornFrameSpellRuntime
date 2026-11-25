using System;
using FixedMathSharp;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     由作者 ID 和作者内部编号组成的全局属性标识。
    /// </summary>
    public readonly struct AttributeId : IEquatable<AttributeId>, IComparable<AttributeId>
    {
        /// <summary>完整的 64 位属性标识。</summary>
        public readonly ulong Value;

        /// <summary>
        ///     使用完整值创建属性标识。
        /// </summary>
        /// <param name="value">完整的 64 位属性标识。</param>
        public AttributeId(ulong value)
        {
            Value = value;
        }

        /// <summary>
        ///     组合作者 ID 和作者内部编号。
        /// </summary>
        /// <param name="authorId">作者 ID。</param>
        /// <param name="localId">作者内部编号。</param>
        /// <returns>组合后的属性标识。</returns>
        public static AttributeId Combine(uint authorId, uint localId)
        {
            return new AttributeId(((ulong)authorId << 32) | localId);
        }

        /// <summary>作者 ID。</summary>
        public uint AuthorId => (uint)(Value >> 32);

        /// <summary>作者内部编号。</summary>
        public uint LocalId => (uint)Value;

        /// <inheritdoc />
        public bool Equals(AttributeId other)
        {
            return Value == other.Value;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is AttributeId other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }

        /// <inheritdoc />
        public int CompareTo(AttributeId other)
        {
            return Value.CompareTo(other.Value);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{AuthorId}:{LocalId}";
        }

        /// <summary>判断两个属性标识是否相等。</summary>
        /// <param name="left">左侧属性标识。</param>
        /// <param name="right">右侧属性标识。</param>
        /// <returns>两个标识相等时为 true。</returns>
        public static bool operator ==(AttributeId left, AttributeId right)
        {
            return left.Equals(right);
        }

        /// <summary>判断两个属性标识是否不相等。</summary>
        /// <param name="left">左侧属性标识。</param>
        /// <param name="right">右侧属性标识。</param>
        /// <returns>两个标识不相等时为 true。</returns>
        public static bool operator !=(AttributeId left, AttributeId right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>属性值的存储类型。</summary>
    public enum AttributeValueKind : byte
    {
        /// <summary>未设置，配置中不得使用。</summary>
        None = 0,

        /// <summary>32 位有符号整数。</summary>
        Int32 = 1,

        /// <summary>64 位有符号整数。</summary>
        Int64 = 2,

        /// <summary>有符号 Q32.32 定点数。</summary>
        Fixed64 = 3,

        /// <summary>布尔值，原始值只能是 0 或 1。</summary>
        Bool = 4
    }

    /// <summary>属性的运行时行为。</summary>
    public enum AttributeBehavior : byte
    {
        /// <summary>未设置，配置中不得使用。</summary>
        None = 0,

        /// <summary>拥有可请求修改的当前值和由修改器计算的最大值。</summary>
        Resource = 1,

        /// <summary>当前值完全由修改器计算。</summary>
        Numeric = 2,

        /// <summary>当前值由覆盖修改器决定。</summary>
        Marker = 3
    }

    /// <summary>属性结算事件所对应的值位置。</summary>
    public enum AttributeValueTarget : byte
    {
        /// <summary>当前值。</summary>
        Current = 0,

        /// <summary>最大值。</summary>
        Max = 1
    }

    /// <summary>
    ///     属性结算事件的来源或请求结果。
    /// </summary>
    public enum AttributeResolvedEventKind : byte
    {
        /// <summary>修改器重算产生了实际属性变化。</summary>
        Recalculated = 0,

        /// <summary>资源当前值请求已经实际写回。</summary>
        RequestApplied = 1,

        /// <summary>资源当前值请求因最终取消 Tag 非空而取消。</summary>
        RequestCancelled = 2,

        /// <summary>资源当前值请求合法，但归一化和限幅后值没有变化。</summary>
        RequestNoChange = 3,

        /// <summary>资源当前值请求的数据在应用阶段无效。</summary>
        RequestRejected = 4
    }

    /// <summary>
    ///     全局属性定义。默认值不属于全局定义，而属于单位预设。
    /// </summary>
    public struct AttributeDefinition : IEquatable<AttributeDefinition>
    {
        /// <summary>属性标识。</summary>
        public AttributeId Id;

        /// <summary>值类型。</summary>
        public AttributeValueKind ValueKind;

        /// <summary>行为分类。</summary>
        public AttributeBehavior Behavior;

        /// <summary>非零时，基础值和最终值不得小于 0。</summary>
        public byte NonNegative;

        /// <summary>非零时，资源最大值变化会按比例调整当前值。</summary>
        public byte ScaleCurrentWithMax;

        /// <inheritdoc />
        public bool Equals(AttributeDefinition other)
        {
            return Id == other.Id
                   && ValueKind == other.ValueKind
                   && Behavior == other.Behavior
                   && NonNegative == other.NonNegative
                   && ScaleCurrentWithMax == other.ScaleCurrentWithMax;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is AttributeDefinition other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Id.GetHashCode();
                hash = (hash * 397) ^ (int)ValueKind;
                hash = (hash * 397) ^ (int)Behavior;
                hash = (hash * 397) ^ NonNegative;
                hash = (hash * 397) ^ ScaleCurrentWithMax;
                return hash;
            }
        }
    }

    /// <summary>
    ///     带类型的属性原始值。
    /// </summary>
    public readonly struct AttributeValue : IEquatable<AttributeValue>
    {
        /// <summary>值类型。</summary>
        public readonly AttributeValueKind Kind;

        /// <summary>按值类型解释的原始位。</summary>
        public readonly long Raw;

        /// <summary>
        ///     创建带类型的原始值。
        /// </summary>
        /// <param name="kind">值类型。</param>
        /// <param name="raw">原始位。</param>
        public AttributeValue(AttributeValueKind kind, long raw)
        {
            Kind = kind;
            Raw = raw;
        }

        /// <summary>
        ///     创建 32 位整数值。
        /// </summary>
        /// <param name="value">整数值。</param>
        /// <returns>属性值。</returns>
        public static AttributeValue FromInt32(int value)
        {
            return new AttributeValue(AttributeValueKind.Int32, value);
        }

        /// <summary>
        ///     创建 64 位整数值。
        /// </summary>
        /// <param name="value">长整数值。</param>
        /// <returns>属性值。</returns>
        public static AttributeValue FromInt64(long value)
        {
            return new AttributeValue(AttributeValueKind.Int64, value);
        }

        /// <summary>
        ///     创建 Q32.32 定点值。
        /// </summary>
        /// <param name="value">定点值。</param>
        /// <returns>属性值。</returns>
        public static AttributeValue FromFixed64(Fixed64 value)
        {
            return new AttributeValue(AttributeValueKind.Fixed64, value.m_rawValue);
        }

        /// <summary>
        ///     创建布尔值。
        /// </summary>
        /// <param name="value">布尔值。</param>
        /// <returns>属性值。</returns>
        public static AttributeValue FromBool(bool value)
        {
            return new AttributeValue(AttributeValueKind.Bool, value ? 1L : 0L);
        }

        /// <inheritdoc />
        public bool Equals(AttributeValue other)
        {
            return Kind == other.Kind && Raw == other.Raw;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is AttributeValue other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Kind * 397) ^ Raw.GetHashCode();
            }
        }
    }

    /// <summary>
    ///     单位预设中的一条属性默认值。
    /// </summary>
    public struct UnitAttributePresetEntry
    {
        /// <summary>属性标识。</summary>
        public AttributeId Id;

        /// <summary>单位的默认值。</summary>
        public AttributeValue DefaultValue;
    }

    /// <summary>
    ///     单位初始化时对资源当前值的一次无事件覆盖。
    /// </summary>
    public struct UnitResourceCurrentOverride
    {
        /// <summary>资源属性标识。</summary>
        public AttributeId Id;

        /// <summary>资源的初始当前值。</summary>
        public AttributeValue CurrentValue;
    }

    /// <summary>
    ///     对外读取的一份属性快照。
    /// </summary>
    public readonly struct AttributeSnapshot
    {
        /// <summary>属性标识。</summary>
        public readonly AttributeId Id;

        /// <summary>值类型。</summary>
        public readonly AttributeValueKind ValueKind;

        /// <summary>行为分类。</summary>
        public readonly AttributeBehavior Behavior;

        /// <summary>默认值原始位。</summary>
        public readonly long DefaultBits;

        /// <summary>基础值原始位。</summary>
        public readonly long BaseBits;

        /// <summary>当前值原始位。</summary>
        public readonly long CurrentBits;

        /// <summary>最大值原始位。</summary>
        public readonly long MaxBits;

        /// <summary>
        ///     创建属性快照。
        /// </summary>
        /// <param name="id">属性标识。</param>
        /// <param name="valueKind">值类型。</param>
        /// <param name="behavior">行为分类。</param>
        /// <param name="defaultBits">默认值原始位。</param>
        /// <param name="baseBits">基础值原始位。</param>
        /// <param name="currentBits">当前值原始位。</param>
        /// <param name="maxBits">最大值原始位。</param>
        public AttributeSnapshot(
            AttributeId id,
            AttributeValueKind valueKind,
            AttributeBehavior behavior,
            long defaultBits,
            long baseBits,
            long currentBits,
            long maxBits)
        {
            Id = id;
            ValueKind = valueKind;
            Behavior = behavior;
            DefaultBits = defaultBits;
            BaseBits = baseBits;
            CurrentBits = currentBits;
            MaxBits = maxBits;
        }
    }
}