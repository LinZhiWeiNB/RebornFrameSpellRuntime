using System;
using FixedMathSharp;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace RebornFrame.Spell.Tests
{
    /// <summary>
    /// 验证 vendored FixedMathSharp 的 Q32.32 行为满足属性系统需要。
    /// </summary>
    public sealed class Fixed64Tests
    {
        [BurstCompile(CompileSynchronously = true)]
        struct FixedMathJob : IJob
        {
            public NativeArray<long> Result;

            public void Execute()
            {
                Fixed64 value = Fixed64.FromRaw(FixedMath.ONE_L + (FixedMath.ONE_L >> 1))
                    * new Fixed64(2);
                Result[0] = value.m_rawValue;
            }
        }

        /// <summary>
        /// 验证中点远离 0 的取整。
        /// </summary>
        [TestCase(1, 4, 0)]
        [TestCase(1, 2, 1)]
        [TestCase(3, 2, 2)]
        [TestCase(-1, 4, 0)]
        [TestCase(-1, 2, -1)]
        [TestCase(-3, 2, -2)]
        public void Round_UsesNearestWithMidpointAwayFromZero(
            long numerator,
            long denominator,
            long expected)
        {
            Fixed64 value = Fixed64.FromRaw(numerator) / Fixed64.FromRaw(denominator);
            long rounded = (long)FixedMath.Round(value, MidpointRounding.AwayFromZero);
            Assert.AreEqual(expected, rounded);
        }

        /// <summary>
        /// 验证乘法在定点域内保留精确结果。
        /// </summary>
        [Test]
        public void Multiply_UsesWideIntermediate()
        {
            Fixed64 oneAndHalf = new Fixed64(3) / new Fixed64(2);
            Fixed64 result = oneAndHalf * new Fixed64(2);

            Assert.AreEqual(new Fixed64(3), result);
        }

        /// <summary>
        /// 验证除法使用确定性的 Q32.32 商。
        /// </summary>
        [Test]
        public void Divide_ReturnsRoundedQ32Value()
        {
            Fixed64 result = Fixed64.One / new Fixed64(3);

            Assert.AreEqual(1_431_655_765L, result.m_rawValue);
        }

        /// <summary>
        /// 验证正向溢出采用饱和而不是回绕。
        /// </summary>
        [Test]
        public void Add_PositiveOverflow_Saturates()
        {
            Fixed64 result = Fixed64.MaxValue + Fixed64.One;

            Assert.AreEqual(long.MaxValue, result.m_rawValue);
        }

        /// <summary>
        /// 验证负向溢出采用饱和而不是回绕。
        /// </summary>
        [Test]
        public void Subtract_NegativeOverflow_Saturates()
        {
            Fixed64 result = Fixed64.MinValue - Fixed64.One;

            Assert.AreEqual(long.MinValue, result.m_rawValue);
        }

        /// <summary>
        /// 验证两个最小值相减不会经过错误的饱和取负路径。
        /// </summary>
        [Test]
        public void Subtract_TwoMinimumValues_ReturnsZero()
        {
            Fixed64 result = Fixed64.MinValue - Fixed64.MinValue;

            Assert.AreEqual(Fixed64.Zero, result);
        }

        /// <summary>
        /// 验证最小值除以负一时饱和到最大值。
        /// </summary>
        [Test]
        public void Divide_MinimumByNegativeOne_Saturates()
        {
            Fixed64 result = Fixed64.MinValue / new Fixed64(-1);

            Assert.AreEqual(Fixed64.MaxValue, result);
        }

        /// <summary>
        /// 验证除零会明确失败。
        /// </summary>
        [Test]
        public void Divide_ByZero_Throws()
        {
            Assert.Throws<DivideByZeroException>(() =>
            {
                _ = Fixed64.One / Fixed64.Zero;
            });
        }

        /// <summary>
        /// 验证同一数值实现可以由 Burst 编译并得到相同原始位。
        /// </summary>
        [Test]
        public void Arithmetic_BurstJob_MatchesManagedResult()
        {
            using var result = new NativeArray<long>(1, Allocator.TempJob);
            new FixedMathJob { Result = result }.Run();

            Assert.AreEqual(new Fixed64(3).m_rawValue, result[0]);
        }
    }
}
