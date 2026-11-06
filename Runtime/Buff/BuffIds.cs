using System;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 由作者 ID 和作者内部编号组成的 Buff 定义标识。
    /// </summary>
    public readonly struct BuffId : IEquatable<BuffId>, IComparable<BuffId>
    {
        /// <summary>完整的 64 位标识。</summary>
        public readonly ulong Value;

        /// <summary>
        /// 使用完整值创建 Buff 标识。
        /// </summary>
        /// <param name="value">完整的 64 位标识。</param>
        public BuffId(ulong value)
        {
            Value = value;
        }

        /// <summary>
        /// 组合作者 ID 和作者内部编号。
        /// </summary>
        /// <param name="authorId">作者 ID。</param>
        /// <param name="localId">作者内部编号。</param>
        /// <returns>组合后的 Buff 标识。</returns>
        public static BuffId Combine(uint authorId, uint localId)
        {
            return new BuffId(((ulong)authorId << 32) | localId);
        }

        /// <summary>作者 ID。</summary>
        public uint AuthorId => (uint)(Value >> 32);

        /// <summary>作者内部编号。</summary>
        public uint LocalId => (uint)Value;

        /// <inheritdoc />
        public bool Equals(BuffId other)
        {
            return Value == other.Value;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is BuffId other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }

        /// <inheritdoc />
        public int CompareTo(BuffId other)
        {
            return Value.CompareTo(other.Value);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{AuthorId}:{LocalId}";
        }

        /// <summary>判断两个 Buff 标识是否相等。</summary>
        /// <param name="left">左侧标识。</param>
        /// <param name="right">右侧标识。</param>
        /// <returns>相等时为 true。</returns>
        public static bool operator ==(BuffId left, BuffId right)
        {
            return left.Equals(right);
        }

        /// <summary>判断两个 Buff 标识是否不相等。</summary>
        /// <param name="left">左侧标识。</param>
        /// <param name="right">右侧标识。</param>
        /// <returns>不相等时为 true。</returns>
        public static bool operator !=(BuffId left, BuffId right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>
    /// 由作者 ID 和作者内部编号组成的 Buff 效果标识。
    /// </summary>
    public readonly struct BuffEffectId : IEquatable<BuffEffectId>, IComparable<BuffEffectId>
    {
        /// <summary>完整的 64 位标识。</summary>
        public readonly ulong Value;

        /// <summary>
        /// 使用完整值创建效果标识。
        /// </summary>
        /// <param name="value">完整的 64 位标识。</param>
        public BuffEffectId(ulong value)
        {
            Value = value;
        }

        /// <summary>
        /// 组合作者 ID 和作者内部编号。
        /// </summary>
        /// <param name="authorId">作者 ID。</param>
        /// <param name="localId">作者内部编号。</param>
        /// <returns>组合后的效果标识。</returns>
        public static BuffEffectId Combine(uint authorId, uint localId)
        {
            return new BuffEffectId(((ulong)authorId << 32) | localId);
        }

        /// <summary>作者 ID。</summary>
        public uint AuthorId => (uint)(Value >> 32);

        /// <summary>作者内部编号。</summary>
        public uint LocalId => (uint)Value;

        /// <inheritdoc />
        public bool Equals(BuffEffectId other)
        {
            return Value == other.Value;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is BuffEffectId other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }

        /// <inheritdoc />
        public int CompareTo(BuffEffectId other)
        {
            return Value.CompareTo(other.Value);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{AuthorId}:{LocalId}";
        }

        /// <summary>判断两个效果标识是否相等。</summary>
        /// <param name="left">左侧标识。</param>
        /// <param name="right">右侧标识。</param>
        /// <returns>相等时为 true。</returns>
        public static bool operator ==(BuffEffectId left, BuffEffectId right)
        {
            return left.Equals(right);
        }

        /// <summary>判断两个效果标识是否不相等。</summary>
        /// <param name="left">左侧标识。</param>
        /// <param name="right">右侧标识。</param>
        /// <returns>不相等时为 true。</returns>
        public static bool operator !=(BuffEffectId left, BuffEffectId right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>
    /// 由作者 ID 和作者内部编号组成的外部信号标识。
    /// </summary>
    public readonly struct BuffSignalId : IEquatable<BuffSignalId>, IComparable<BuffSignalId>
    {
        /// <summary>完整的 64 位标识。</summary>
        public readonly ulong Value;

        /// <summary>
        /// 使用完整值创建信号标识。
        /// </summary>
        /// <param name="value">完整的 64 位标识。</param>
        public BuffSignalId(ulong value)
        {
            Value = value;
        }

        /// <summary>
        /// 组合作者 ID 和作者内部编号。
        /// </summary>
        /// <param name="authorId">作者 ID。</param>
        /// <param name="localId">作者内部编号。</param>
        /// <returns>组合后的信号标识。</returns>
        public static BuffSignalId Combine(uint authorId, uint localId)
        {
            return new BuffSignalId(((ulong)authorId << 32) | localId);
        }

        /// <summary>作者 ID。</summary>
        public uint AuthorId => (uint)(Value >> 32);

        /// <summary>作者内部编号。</summary>
        public uint LocalId => (uint)Value;

        /// <inheritdoc />
        public bool Equals(BuffSignalId other)
        {
            return Value == other.Value;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is BuffSignalId other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }

        /// <inheritdoc />
        public int CompareTo(BuffSignalId other)
        {
            return Value.CompareTo(other.Value);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{AuthorId}:{LocalId}";
        }

        /// <summary>判断两个信号标识是否相等。</summary>
        /// <param name="left">左侧标识。</param>
        /// <param name="right">右侧标识。</param>
        /// <returns>相等时为 true。</returns>
        public static bool operator ==(BuffSignalId left, BuffSignalId right)
        {
            return left.Equals(right);
        }

        /// <summary>判断两个信号标识是否不相等。</summary>
        /// <param name="left">左侧标识。</param>
        /// <param name="right">右侧标识。</param>
        /// <returns>不相等时为 true。</returns>
        public static bool operator !=(BuffSignalId left, BuffSignalId right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>
    /// 由作者 ID 和作者内部编号组成的步进时钟种类。
    /// </summary>
    public readonly struct BuffIteratorId : IEquatable<BuffIteratorId>, IComparable<BuffIteratorId>
    {
        /// <summary>完整的 64 位标识。</summary>
        public readonly ulong Value;

        /// <summary>
        /// 使用完整值创建迭代器标识。
        /// </summary>
        /// <param name="value">完整的 64 位标识。</param>
        public BuffIteratorId(ulong value)
        {
            Value = value;
        }

        /// <summary>
        /// 组合作者 ID 和作者内部编号。
        /// </summary>
        /// <param name="authorId">作者 ID。</param>
        /// <param name="localId">作者内部编号。</param>
        /// <returns>组合后的迭代器标识。</returns>
        public static BuffIteratorId Combine(uint authorId, uint localId)
        {
            return new BuffIteratorId(((ulong)authorId << 32) | localId);
        }

        /// <summary>作者 ID。</summary>
        public uint AuthorId => (uint)(Value >> 32);

        /// <summary>作者内部编号。</summary>
        public uint LocalId => (uint)Value;

        /// <inheritdoc />
        public bool Equals(BuffIteratorId other)
        {
            return Value == other.Value;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is BuffIteratorId other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }

        /// <inheritdoc />
        public int CompareTo(BuffIteratorId other)
        {
            return Value.CompareTo(other.Value);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{AuthorId}:{LocalId}";
        }

        /// <summary>判断两个迭代器标识是否相等。</summary>
        /// <param name="left">左侧标识。</param>
        /// <param name="right">右侧标识。</param>
        /// <returns>相等时为 true。</returns>
        public static bool operator ==(BuffIteratorId left, BuffIteratorId right)
        {
            return left.Equals(right);
        }

        /// <summary>判断两个迭代器标识是否不相等。</summary>
        /// <param name="left">左侧标识。</param>
        /// <param name="right">右侧标识。</param>
        /// <returns>不相等时为 true。</returns>
        public static bool operator !=(BuffIteratorId left, BuffIteratorId right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>
    /// 精确步进时钟。ScopeStableId 为 0 时表示全局时钟，不是全体单位的通配。
    /// </summary>
    public readonly struct BuffClockKey : IEquatable<BuffClockKey>, IComparable<BuffClockKey>
    {
        /// <summary>时钟种类。</summary>
        public readonly BuffIteratorId IteratorId;

        /// <summary>时钟作用域。0 表示全局时钟。</summary>
        public readonly ulong ScopeStableId;

        /// <summary>
        /// 创建精确时钟键。
        /// </summary>
        /// <param name="iteratorId">时钟种类。</param>
        /// <param name="scopeStableId">作用域稳定 ID。全局时钟使用 0。</param>
        public BuffClockKey(BuffIteratorId iteratorId, ulong scopeStableId)
        {
            IteratorId = iteratorId;
            ScopeStableId = scopeStableId;
        }

        /// <inheritdoc />
        public bool Equals(BuffClockKey other)
        {
            return IteratorId == other.IteratorId && ScopeStableId == other.ScopeStableId;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is BuffClockKey other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return IteratorId.GetHashCode() ^ ScopeStableId.GetHashCode();
        }

        /// <inheritdoc />
        public int CompareTo(BuffClockKey other)
        {
            int iterator = IteratorId.CompareTo(other.IteratorId);
            if (iterator != 0)
            {
                return iterator;
            }

            return ScopeStableId.CompareTo(other.ScopeStableId);
        }

        /// <summary>判断两个时钟键是否相等。</summary>
        /// <param name="left">左侧时钟。</param>
        /// <param name="right">右侧时钟。</param>
        /// <returns>相等时为 true。</returns>
        public static bool operator ==(BuffClockKey left, BuffClockKey right)
        {
            return left.Equals(right);
        }

        /// <summary>判断两个时钟键是否不相等。</summary>
        /// <param name="left">左侧时钟。</param>
        /// <param name="right">右侧时钟。</param>
        /// <returns>不相等时为 true。</returns>
        public static bool operator !=(BuffClockKey left, BuffClockKey right)
        {
            return !left.Equals(right);
        }
    }
}
