using System;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     <see cref="LibraryCondition.Tag" /> 的参数布局。
    ///     第 0 字节是 <see cref="TagMatch" />，第 1 字节是查询 Tag 数量，随后每个 Tag 占 8 个小端字节。
    ///     目标携带的任一 Tag 命中其中任一查询 Tag 时，该条件通过。
    /// </summary>
    public static class ConditionTagParameters
    {
        /// <summary>
        ///     写成一条 Tag 条件的参数。
        /// </summary>
        /// <param name="mode">层级匹配方式。</param>
        /// <param name="tags">查询 Tag。至少一个，至多 255 个。</param>
        /// <returns>可写入条件参数的字节。</returns>
        /// <exception cref="ArgumentNullException">Tag 列表为 null。</exception>
        /// <exception cref="ArgumentException">匹配方式无效，或 Tag 数量超出范围。</exception>
        public static byte[] Write(TagMatch mode, params TagId[] tags)
        {
            if (tags == null) throw new ArgumentNullException(nameof(tags));

            if (!IsHierarchy(mode) || tags.Length == 0 || tags.Length > byte.MaxValue)
                throw new ArgumentException("Tag 条件参数无效。", nameof(tags));

            var bytes = new byte[2 + tags.Length * 8];
            bytes[0] = (byte)mode;
            bytes[1] = (byte)tags.Length;
            for (var index = 0; index < tags.Length; index++)
                WriteUInt64(bytes, 2 + index * 8, tags[index].Value);

            return bytes;
        }

        /// <summary>
        ///     检查参数是否是合法的 Tag 条件。
        /// </summary>
        /// <param name="bytes">条件参数。null 视为空。</param>
        /// <returns>布局、匹配方式和 Tag 都合法时为 true。</returns>
        public static bool IsValid(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 2 || !IsHierarchy((TagMatch)bytes[0])) return false;

            var count = bytes[1];
            if (count == 0 || bytes.Length != 2 + count * 8) return false;

            for (var index = 0; index < count; index++)
                if (new TagId(ReadUInt64(bytes, 2 + index * 8)) == LibraryTags.None)
                    return false;

            return true;
        }

        internal static bool IsHierarchy(TagMatch mode)
        {
            return mode == TagMatch.Exact
                   || mode == TagMatch.Upward
                   || mode == TagMatch.Downward
                   || mode == TagMatch.Any;
        }

        internal static void WriteUInt64(byte[] bytes, int offset, ulong value)
        {
            for (var shift = 0; shift < 8; shift++) bytes[offset + shift] = (byte)(value >> (8 * shift));
        }

        internal static ulong ReadUInt64(byte[] bytes, int offset)
        {
            ulong value = 0;
            for (var shift = 0; shift < 8; shift++) value |= (ulong)bytes[offset + shift] << (8 * shift);

            return value;
        }
    }
}
