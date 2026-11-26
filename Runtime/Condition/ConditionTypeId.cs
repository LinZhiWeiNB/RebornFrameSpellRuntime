using System;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     由作者 ID 和作者内部编号组成的条件类型。
    /// </summary>
    public readonly struct ConditionTypeId : IEquatable<ConditionTypeId>, IComparable<ConditionTypeId>
    {
        /// <summary>完整的 64 位条件类型。</summary>
        public readonly ulong Value;

        /// <summary>
        ///     使用完整值创建条件类型。
        /// </summary>
        /// <param name="value">完整的 64 位条件类型。</param>
        public ConditionTypeId(ulong value)
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
        /// <returns>组合后的条件类型。</returns>
        public static ConditionTypeId Combine(uint authorId, uint localId)
        {
            return new ConditionTypeId(((ulong)authorId << 32) | localId);
        }

        /// <inheritdoc />
        public bool Equals(ConditionTypeId other)
        {
            return Value == other.Value;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is ConditionTypeId other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }

        /// <inheritdoc />
        public int CompareTo(ConditionTypeId other)
        {
            return Value.CompareTo(other.Value);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{AuthorId}:{LocalId}";
        }

        /// <summary>判断两个条件类型是否相等。</summary>
        /// <param name="left">左侧类型。</param>
        /// <param name="right">右侧类型。</param>
        /// <returns>相等时为 true。</returns>
        public static bool operator ==(ConditionTypeId left, ConditionTypeId right)
        {
            return left.Equals(right);
        }

        /// <summary>判断两个条件类型是否不相等。</summary>
        /// <param name="left">左侧类型。</param>
        /// <param name="right">右侧类型。</param>
        /// <returns>不相等时为 true。</returns>
        public static bool operator !=(ConditionTypeId left, ConditionTypeId right)
        {
            return !left.Equals(right);
        }
    }
}
