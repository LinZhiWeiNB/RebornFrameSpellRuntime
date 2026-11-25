using System;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     由作者 ID 和作者内部编号组成的法术定义标识。
    /// </summary>
    public readonly struct SpellId : IEquatable<SpellId>, IComparable<SpellId>
    {
        /// <summary>完整的 64 位标识。</summary>
        public readonly ulong Value;

        /// <summary>
        ///     使用完整值创建法术标识。
        /// </summary>
        /// <param name="value">完整的 64 位标识。</param>
        public SpellId(ulong value)
        {
            Value = value;
        }

        /// <summary>作者 ID。</summary>
        public uint AuthorId => (uint)(Value >> 32);

        /// <summary>作者内部编号。</summary>
        public uint LocalId => (uint)Value;

        /// <summary>
        ///     组合作者 ID 和作者内部编号。
        /// </summary>
        /// <param name="authorId">作者 ID。</param>
        /// <param name="localId">作者内部编号。</param>
        /// <returns>组合后的法术标识。</returns>
        public static SpellId Combine(uint authorId, uint localId)
        {
            return new SpellId(((ulong)authorId << 32) | localId);
        }

        /// <inheritdoc />
        public bool Equals(SpellId other)
        {
            return Value == other.Value;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is SpellId other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }

        /// <inheritdoc />
        public int CompareTo(SpellId other)
        {
            return Value.CompareTo(other.Value);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{AuthorId}:{LocalId}";
        }

        /// <summary>判断两个法术标识是否相等。</summary>
        /// <param name="left">左侧标识。</param>
        /// <param name="right">右侧标识。</param>
        /// <returns>相等时为 true。</returns>
        public static bool operator ==(SpellId left, SpellId right)
        {
            return left.Equals(right);
        }

        /// <summary>判断两个法术标识是否不相等。</summary>
        /// <param name="left">左侧标识。</param>
        /// <param name="right">右侧标识。</param>
        /// <returns>不相等时为 true。</returns>
        public static bool operator !=(SpellId left, SpellId right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>
    ///     由作者 ID 和作者内部编号组成的目标拾取标识。
    /// </summary>
    public readonly struct SpellPickId : IEquatable<SpellPickId>, IComparable<SpellPickId>
    {
        /// <summary>完整的 64 位标识。</summary>
        public readonly ulong Value;

        /// <summary>
        ///     使用完整值创建拾取标识。
        /// </summary>
        /// <param name="value">完整的 64 位标识。</param>
        public SpellPickId(ulong value)
        {
            Value = value;
        }

        /// <summary>作者 ID。</summary>
        public uint AuthorId => (uint)(Value >> 32);

        /// <summary>作者内部编号。</summary>
        public uint LocalId => (uint)Value;

        /// <summary>
        ///     组合作者 ID 和作者内部编号。
        /// </summary>
        /// <param name="authorId">作者 ID。</param>
        /// <param name="localId">作者内部编号。</param>
        /// <returns>组合后的拾取标识。</returns>
        public static SpellPickId Combine(uint authorId, uint localId)
        {
            return new SpellPickId(((ulong)authorId << 32) | localId);
        }

        /// <inheritdoc />
        public bool Equals(SpellPickId other)
        {
            return Value == other.Value;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is SpellPickId other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }

        /// <inheritdoc />
        public int CompareTo(SpellPickId other)
        {
            return Value.CompareTo(other.Value);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{AuthorId}:{LocalId}";
        }

        /// <summary>判断两个拾取标识是否相等。</summary>
        /// <param name="left">左侧标识。</param>
        /// <param name="right">右侧标识。</param>
        /// <returns>相等时为 true。</returns>
        public static bool operator ==(SpellPickId left, SpellPickId right)
        {
            return left.Equals(right);
        }

        /// <summary>判断两个拾取标识是否不相等。</summary>
        /// <param name="left">左侧标识。</param>
        /// <param name="right">右侧标识。</param>
        /// <returns>不相等时为 true。</returns>
        public static bool operator !=(SpellPickId left, SpellPickId right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>
    ///     由作者 ID 和作者内部编号组成的表现轨道种类。
    /// </summary>
    public readonly struct SpellPresentationTrackId : IEquatable<SpellPresentationTrackId>, IComparable<SpellPresentationTrackId>
    {
        /// <summary>完整的 64 位标识。</summary>
        public readonly ulong Value;

        /// <summary>
        ///     使用完整值创建轨道种类。
        /// </summary>
        /// <param name="value">完整的 64 位标识。</param>
        public SpellPresentationTrackId(ulong value)
        {
            Value = value;
        }

        /// <summary>作者 ID。</summary>
        public uint AuthorId => (uint)(Value >> 32);

        /// <summary>作者内部编号。</summary>
        public uint LocalId => (uint)Value;

        /// <summary>
        ///     组合作者 ID 和作者内部编号。
        /// </summary>
        /// <param name="authorId">作者 ID。</param>
        /// <param name="localId">作者内部编号。</param>
        /// <returns>组合后的轨道种类。</returns>
        public static SpellPresentationTrackId Combine(uint authorId, uint localId)
        {
            return new SpellPresentationTrackId(((ulong)authorId << 32) | localId);
        }

        /// <inheritdoc />
        public bool Equals(SpellPresentationTrackId other)
        {
            return Value == other.Value;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is SpellPresentationTrackId other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }

        /// <inheritdoc />
        public int CompareTo(SpellPresentationTrackId other)
        {
            return Value.CompareTo(other.Value);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{AuthorId}:{LocalId}";
        }

        /// <summary>判断两个轨道种类是否相等。</summary>
        /// <param name="left">左侧种类。</param>
        /// <param name="right">右侧种类。</param>
        /// <returns>相等时为 true。</returns>
        public static bool operator ==(SpellPresentationTrackId left, SpellPresentationTrackId right)
        {
            return left.Equals(right);
        }

        /// <summary>判断两个轨道种类是否不相等。</summary>
        /// <param name="left">左侧种类。</param>
        /// <param name="right">右侧种类。</param>
        /// <returns>不相等时为 true。</returns>
        public static bool operator !=(SpellPresentationTrackId left, SpellPresentationTrackId right)
        {
            return !left.Equals(right);
        }
    }
}
