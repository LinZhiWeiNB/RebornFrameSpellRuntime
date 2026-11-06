using System;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 由作者 ID 和作者内部编号组成的全局 Tag 标识。
    /// </summary>
    public readonly struct TagId : IEquatable<TagId>, IComparable<TagId>
    {
        /// <summary>完整的 64 位 Tag 标识。</summary>
        public readonly ulong Value;

        /// <summary>
        /// 使用完整值创建 Tag 标识。
        /// </summary>
        /// <param name="value">完整的 64 位 Tag 标识。</param>
        public TagId(ulong value)
        {
            Value = value;
        }

        /// <summary>
        /// 组合作者 ID 和作者内部编号。
        /// </summary>
        /// <param name="authorId">作者 ID。</param>
        /// <param name="localId">作者内部编号。</param>
        /// <returns>组合后的 Tag 标识。</returns>
        public static TagId Combine(uint authorId, uint localId)
        {
            return new TagId(((ulong)authorId << 32) | localId);
        }

        /// <summary>作者 ID。</summary>
        public uint AuthorId => (uint)(Value >> 32);

        /// <summary>作者内部编号。</summary>
        public uint LocalId => (uint)Value;

        /// <inheritdoc />
        public bool Equals(TagId other)
        {
            return Value == other.Value;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is TagId other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }

        /// <inheritdoc />
        public int CompareTo(TagId other)
        {
            return Value.CompareTo(other.Value);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{AuthorId}:{LocalId}";
        }

        /// <summary>判断两个 Tag 标识是否相等。</summary>
        /// <param name="left">左侧标识。</param>
        /// <param name="right">右侧标识。</param>
        /// <returns>两个标识相等时为 true。</returns>
        public static bool operator ==(TagId left, TagId right)
        {
            return left.Equals(right);
        }

        /// <summary>判断两个 Tag 标识是否不相等。</summary>
        /// <param name="left">左侧标识。</param>
        /// <param name="right">右侧标识。</param>
        /// <returns>两个标识不相等时为 true。</returns>
        public static bool operator !=(TagId left, TagId right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>
    /// 全局 Tag 定义。层级通过父节点表达，类似 GAS 的点分继承。
    /// </summary>
    public struct TagDefinition : IEquatable<TagDefinition>
    {
        /// <summary>Tag 标识。</summary>
        public TagId Id;

        /// <summary>父 Tag；<see cref="LibraryTags.None"/> 表示根节点。</summary>
        public TagId ParentId;

        /// <inheritdoc />
        public bool Equals(TagDefinition other)
        {
            return Id == other.Id && ParentId == other.ParentId;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is TagDefinition other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                return (Id.GetHashCode() * 397) ^ ParentId.GetHashCode();
            }
        }
    }
}
