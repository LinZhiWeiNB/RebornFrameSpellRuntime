using NUnit.Framework;

namespace RebornFrame.Spell.Tests
{
    /// <summary>
    /// 自定义 128 位中间运算的边界测试。
    /// </summary>
    public sealed class WideMathTests
    {
        /// <summary>
        /// 验证需要高 64 位的乘积可以正确完成除法。
        /// </summary>
        [Test]
        public void MultiplyDivideRounded_WideProduct_ReturnsExpectedValue()
        {
            const long value = 4_000_000_000L;

            long result = WideMath.MultiplyDivideRounded(value, value, value);

            Assert.AreEqual(value, result);
        }

        /// <summary>
        /// 验证正负中点都向远离 0 的方向取整。
        /// </summary>
        [TestCase(3, 5, 2, 8)]
        [TestCase(-3, 5, 2, -8)]
        [TestCase(3, -5, 2, -8)]
        [TestCase(3, 5, -2, -8)]
        public void MultiplyDivideRounded_Midpoint_RoundsAwayFromZero(
            long left,
            long right,
            long divisor,
            long expected)
        {
            Assert.AreEqual(expected, WideMath.MultiplyDivideRounded(left, right, divisor));
        }

        /// <summary>
        /// 验证最小值绝对值为 2 的 63 次方时仍能参与运算。
        /// </summary>
        [Test]
        public void MultiplyDivideRounded_MinimumDividedByMinimum_ReturnsOne()
        {
            long result = WideMath.MultiplyDivideRounded(long.MinValue, 1, long.MinValue);

            Assert.AreEqual(1, result);
        }

        /// <summary>
        /// 验证无法表示的正结果饱和到最大值。
        /// </summary>
        [Test]
        public void MultiplyDivideRounded_PositiveOverflow_Saturates()
        {
            long result = WideMath.MultiplyDivideRounded(long.MinValue, -1, 1);

            Assert.AreEqual(long.MaxValue, result);
        }

        /// <summary>
        /// 验证无法表示的负结果饱和到最小值。
        /// </summary>
        [Test]
        public void MultiplyDivideRounded_NegativeOverflow_Saturates()
        {
            long result = WideMath.MultiplyDivideRounded(long.MinValue, 2, 1);

            Assert.AreEqual(long.MinValue, result);
        }
    }
}
