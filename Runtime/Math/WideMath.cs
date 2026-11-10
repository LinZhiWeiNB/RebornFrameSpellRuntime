using System.Runtime.CompilerServices;

namespace RebornFrame.Spell
{
    /// <summary>
    /// Burst 可执行的确定性宽整数运算。定点 Q32.32 走 FixedMathSharp；
    /// 本类型只处理 Int32/Int64 饱和、修改器宽累加和整数乘除。
    /// 乘除在无符号绝对值空间完成，最后恢复符号，避免依赖平台的有符号溢出行为。
    /// </summary>
    internal static class WideMath
    {
        readonly struct UInt128Value
        {
            internal readonly ulong High;
            internal readonly ulong Low;

            internal UInt128Value(ulong high, ulong low)
            {
                High = high;
                Low = low;
            }
        }

        /// <summary>
        /// 以二进制补码保存有符号 128 位合计。DynamicBuffer 的长度上限为
        /// 32 位整数，因此把任意数量的 64 位修改器相加也不会溢出该范围。
        /// </summary>
        internal struct SignedAccumulator
        {
            long _high;
            ulong _low;

            /// <summary>
            /// 加入一个 64 位有符号值；先符号扩展，再传播低字进位。
            /// </summary>
            /// <param name="value">待加入的值。</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void Add(long value)
            {
                ulong previousLow = _low;
                _low += (ulong)value;
                _high += value < 0 ? -1L : 0L;
                if (_low < previousLow)
                {
                    _high++;
                }
            }

            /// <summary>
            /// 减去一个 64 位有符号值。与 <see cref="Add"/> 互为逆操作，
            /// <see cref="long.MinValue"/> 不需要先取负。
            /// </summary>
            /// <param name="value">待减去的值。</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void Subtract(long value)
            {
                ulong previousLow = _low;
                _low -= (ulong)value;
                _high -= value < 0 ? -1L : 0L;
                if (_low > previousLow)
                {
                    _high--;
                }
            }

            /// <summary>
            /// 在全部值合计后一次性饱和到 64 位，避免结果受缓冲物理顺序影响。
            /// </summary>
            /// <returns>饱和后的 64 位合计。</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal long ToInt64Saturated()
            {
                if (_high == 0 && _low <= long.MaxValue)
                {
                    return (long)_low;
                }

                if (_high == -1 && _low >= 0x8000_0000_0000_0000UL)
                {
                    return (long)_low;
                }

                return _high < 0 ? long.MinValue : long.MaxValue;
            }
        }

        /// <summary>
        /// 对两个 64 位整数做饱和加法。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static long SaturatingAdd(long left, long right)
        {
            if (right > 0 && left > long.MaxValue - right)
            {
                return long.MaxValue;
            }

            if (right < 0 && left < long.MinValue - right)
            {
                return long.MinValue;
            }

            return left + right;
        }

        /// <summary>
        /// 对两个 64 位整数做饱和减法。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static long SaturatingSubtract(long left, long right)
        {
            if (right > 0 && left < long.MinValue + right)
            {
                return long.MinValue;
            }

            if (right < 0 && left > long.MaxValue + right)
            {
                return long.MaxValue;
            }

            return left - right;
        }

        /// <summary>
        /// 对 64 位整数做饱和取负。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static long SaturatingNegate(long value)
        {
            return value == long.MinValue ? long.MaxValue : -value;
        }

        /// <summary>
        /// 计算 <c>left × right ÷ divisor</c>，使用四舍五入且中点远离 0，
        /// 并把结果饱和到 64 位有符号范围。
        /// </summary>
        /// <param name="left">第一个乘数。</param>
        /// <param name="right">第二个乘数。</param>
        /// <param name="divisor">非零除数。</param>
        /// <returns>取整并饱和后的结果。</returns>
        internal static long MultiplyDivideRounded(long left, long right, long divisor)
        {
            if (divisor == 0)
            {
                return 0;
            }

            if (left == 0 || right == 0)
            {
                return 0;
            }

            bool negative = (left < 0) ^ (right < 0) ^ (divisor < 0);
            UInt128Value product = MultiplyUnsigned(Magnitude(left), Magnitude(right));
            UInt128Value quotient = DivideUnsigned(product, Magnitude(divisor), out ulong remainder);

            ulong divisorMagnitude = Magnitude(divisor);
            if (ShouldRoundUp(remainder, divisorMagnitude))
            {
                quotient = Increment(quotient);
            }

            return ToSignedSaturated(quotient, negative);
        }

        /// <summary>
        /// 把 64 位结果限制到 32 位有符号整数范围。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int ToInt32Saturated(long value)
        {
            if (value >= int.MaxValue)
            {
                return int.MaxValue;
            }

            if (value <= int.MinValue)
            {
                return int.MinValue;
            }

            return (int)value;
        }

        /// <summary>
        /// 按属性类型验证原始值是否合法。
        /// </summary>
        /// <param name="kind">属性值类型。</param>
        /// <param name="raw">原始值。</param>
        /// <returns>原始值可以由该类型表示时为 true。</returns>
        internal static bool IsRawValueValid(AttributeValueKind kind, long raw)
        {
            switch (kind)
            {
                case AttributeValueKind.Int32:
                    return raw >= int.MinValue && raw <= int.MaxValue;
                case AttributeValueKind.Int64:
                case AttributeValueKind.Fixed64:
                    return true;
                case AttributeValueKind.Bool:
                    return raw == 0 || raw == 1;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 把原始值归一化到属性类型范围，并按需应用非负约束。
        /// </summary>
        /// <param name="kind">属性值类型。</param>
        /// <param name="raw">待归一化的原始值。</param>
        /// <param name="nonNegative">是否限制为非负。</param>
        /// <returns>归一化后的原始值。</returns>
        internal static long Normalize(AttributeValueKind kind, long raw, bool nonNegative)
        {
            long normalized;
            switch (kind)
            {
                case AttributeValueKind.Int32:
                    normalized = ToInt32Saturated(raw);
                    break;
                case AttributeValueKind.Int64:
                case AttributeValueKind.Fixed64:
                    normalized = raw;
                    break;
                case AttributeValueKind.Bool:
                    normalized = raw == 0 ? 0 : 1;
                    break;
                default:
                    return 0;
            }

            return nonNegative && normalized < 0 ? 0 : normalized;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong Magnitude(long value)
        {
            ulong bits = (ulong)value;
            return value < 0 ? ~bits + 1UL : bits;
        }

        // 采用 32 位分块乘法，避免依赖 System.Int128；该算法在 Burst、Mono 和 IL2CPP
        // 中只使用定义良好的无符号运算。
        static UInt128Value MultiplyUnsigned(ulong left, ulong right)
        {
            const ulong mask = uint.MaxValue;
            ulong leftLow = left & mask;
            ulong leftHigh = left >> 32;
            ulong rightLow = right & mask;
            ulong rightHigh = right >> 32;

            ulong product = leftLow * rightLow;
            ulong lowLow = product & mask;
            ulong carry = product >> 32;

            product = leftHigh * rightLow + carry;
            ulong middleLow = product & mask;
            ulong middleHigh = product >> 32;

            product = leftLow * rightHigh + middleLow;
            ulong high = leftHigh * rightHigh + middleHigh + (product >> 32);
            ulong low = (product << 32) + lowLow;
            return new UInt128Value(high, low);
        }

        // 位除法固定执行 128 轮，不依赖 CPU 的宽整数支持。属性缩放和 Fixed 除法
        // 不是每帧每属性必经路径，优先保证跨平台结果一致。
        static UInt128Value DivideUnsigned(UInt128Value dividend, ulong divisor, out ulong remainder)
        {
            ulong quotientHigh = 0;
            ulong quotientLow = 0;
            ulong remainderHigh = 0;
            ulong remainderLow = 0;

            for (int bit = 127; bit >= 0; bit--)
            {
                remainderHigh = remainderLow >> 63;
                remainderLow <<= 1;
                if (GetBit(dividend, bit))
                {
                    remainderLow |= 1;
                }

                if (remainderHigh != 0 || remainderLow >= divisor)
                {
                    ulong previousLow = remainderLow;
                    remainderLow -= divisor;
                    if (previousLow < divisor)
                    {
                        remainderHigh--;
                    }

                    if (bit >= 64)
                    {
                        quotientHigh |= 1UL << (bit - 64);
                    }
                    else
                    {
                        quotientLow |= 1UL << bit;
                    }
                }
            }

            remainder = remainderLow;
            return new UInt128Value(quotientHigh, quotientLow);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool GetBit(UInt128Value value, int bit)
        {
            return bit >= 64
                ? ((value.High >> (bit - 64)) & 1UL) != 0
                : ((value.Low >> bit) & 1UL) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool ShouldRoundUp(ulong remainder, ulong divisor)
        {
            ulong half = divisor >> 1;
            return remainder > half || ((divisor & 1UL) == 0 && remainder == half);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static UInt128Value Increment(UInt128Value value)
        {
            ulong low = value.Low + 1;
            ulong high = value.High + (low == 0 ? 1UL : 0UL);
            return new UInt128Value(high, low);
        }

        static long ToSignedSaturated(UInt128Value magnitude, bool negative)
        {
            if (!negative)
            {
                return magnitude.High != 0 || magnitude.Low > long.MaxValue
                    ? long.MaxValue
                    : (long)magnitude.Low;
            }

            const ulong negativeLimit = 1UL << 63;
            if (magnitude.High != 0 || magnitude.Low >= negativeLimit)
            {
                return long.MinValue;
            }

            return -(long)magnitude.Low;
        }
    }
}
