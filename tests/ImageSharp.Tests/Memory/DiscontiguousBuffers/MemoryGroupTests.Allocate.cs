﻿// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Memory.Internals;
using Xunit;
using Xunit.Abstractions;

namespace SixLabors.ImageSharp.Tests.Memory.DiscontiguousBuffers
{
    public partial class MemoryGroupTests
    {
        public class Allocate : MemoryGroupTestsBase
        {
#pragma warning disable SA1509
            public static TheoryData<object, int, int, long, int, int, int> AllocateData =
                new TheoryData<object, int, int, long, int, int, int>()
                {
                    { default(S5), 22, 4, 4, 1, 4, 4 },
                    { default(S5), 22, 4, 7, 2, 4, 3 },
                    { default(S5), 22, 4, 8, 2, 4, 4 },
                    { default(S5), 22, 4, 21, 6, 4, 1 },

                    // empty:
                    { default(S5), 22, 0, 0, 1, -1, 0 },
                    { default(S5), 22, 4, 0, 1, -1, 0 },

                    { default(S4), 50, 12, 12, 1, 12, 12 },
                    { default(S4), 50, 7, 12, 2, 7, 5 },
                    { default(S4), 50, 6, 12, 1, 12, 12 },
                    { default(S4), 50, 5, 12, 2, 10, 2 },
                    { default(S4), 50, 4, 12, 1, 12, 12 },
                    { default(S4), 50, 3, 12, 1, 12, 12 },
                    { default(S4), 50, 2, 12, 1, 12, 12 },
                    { default(S4), 50, 1, 12, 1, 12, 12 },

                    { default(S4), 50, 12, 13, 2, 12, 1 },
                    { default(S4), 50, 7, 21, 3, 7, 7 },
                    { default(S4), 50, 7, 23, 4, 7, 2 },
                    { default(S4), 50, 6, 13, 2, 12, 1 },
                    { default(S4), 1024, 20, 800, 4, 240, 80 },

                    { default(short), 200, 50, 49, 1, 49, 49 },
                    { default(short), 200, 50, 1, 1, 1, 1 },
                    { default(byte), 1000, 512, 2047, 4, 512, 511 }
                };

            [Theory]
            [MemberData(nameof(AllocateData))]
            public void Allocate_FromMemoryAllocator_BufferSizesAreCorrect<T>(
                T dummy,
                int bufferCapacity,
                int bufferAlignment,
                long totalLength,
                int expectedNumberOfBuffers,
                int expectedBufferSize,
                int expectedSizeOfLastBuffer)
                where T : struct
            {
                this.MemoryAllocator.BufferCapacityInBytes = bufferCapacity;

                // Act:
                using var g = MemoryGroup<T>.Allocate(this.MemoryAllocator, totalLength, bufferAlignment);

                // Assert:
                ValidateAllocateMemoryGroup(expectedNumberOfBuffers, expectedBufferSize, expectedSizeOfLastBuffer, g);
            }

            [Theory]
            [MemberData(nameof(AllocateData))]
            public void Allocate_FromPool_BufferSizesAreCorrect<T>(
                T dummy,
                int bufferCapacity,
                int bufferAlignment,
                long totalLength,
                int expectedNumberOfBuffers,
                int expectedBufferSize,
                int expectedSizeOfLastBuffer)
                where T : struct
            {
                if (totalLength == 0)
                {
                    // Invalid case for UniformByteArrayPool allocations
                    return;
                }

                var pool = new UniformUnmanagedMemoryPool(bufferCapacity, expectedNumberOfBuffers);

                // Act:
                using var g = MemoryGroup<T>.Allocate(pool, totalLength, bufferAlignment);

                // Assert:
                ValidateAllocateMemoryGroup(expectedNumberOfBuffers, expectedBufferSize, expectedSizeOfLastBuffer, g);
            }

            private static unsafe Span<byte> GetSpan(UniformUnmanagedMemoryPool pool, UnmanagedMemoryHandle h) =>
                new Span<byte>((void*)h.DangerousGetHandle(), pool.BufferLength);

            [Theory]
            [InlineData(AllocationOptions.None)]
            [InlineData(AllocationOptions.Clean)]
            public void Allocate_FromPool_AllocationOptionsAreApplied(AllocationOptions options)
            {
                var pool = new UniformUnmanagedMemoryPool(10, 5);
                UnmanagedMemoryHandle[] buffers = pool.Rent(5);
                foreach (UnmanagedMemoryHandle b in buffers)
                {
                    GetSpan(pool, b).Fill(42);
                }

                pool.Return(buffers);

                using var g = MemoryGroup<byte>.Allocate(pool, 50, 10, options);
                Span<byte> expected = stackalloc byte[10];
                expected.Fill((byte)(options == AllocationOptions.Clean ? 0 : 42));
                foreach (Memory<byte> memory in g)
                {
                    Assert.True(expected.SequenceEqual(memory.Span));
                }
            }

            [Theory]
            [InlineData(64, 4, 60, 240, false)]
            [InlineData(64, 4, 60, 244, true)]
            public void Allocate_FromPool_AroundLimit(
                int bufferCapacityBytes,
                int poolCapacity,
                int alignmentBytes,
                int requestBytes,
                bool shouldReturnNull)
            {
                var pool = new UniformUnmanagedMemoryPool(bufferCapacityBytes, poolCapacity);
                int alignmentElements = alignmentBytes / Unsafe.SizeOf<S4>();
                int requestElements = requestBytes / Unsafe.SizeOf<S4>();

                using var g = MemoryGroup<S4>.Allocate(pool, requestElements, alignmentElements);
                if (shouldReturnNull)
                {
                    Assert.Null(g);
                }
                else
                {
                    Assert.NotNull(g);
                }
            }

            internal static void ValidateAllocateMemoryGroup<T>(
                int expectedNumberOfBuffers,
                int expectedBufferSize,
                int expectedSizeOfLastBuffer,
                MemoryGroup<T> g)
                where T : struct
            {
                Assert.Equal(expectedNumberOfBuffers, g.Count);

                if (expectedBufferSize >= 0)
                {
                    Assert.Equal(expectedBufferSize, g.BufferLength);
                }

                if (g.Count == 0)
                {
                    return;
                }

                for (int i = 0; i < g.Count - 1; i++)
                {
                    Assert.Equal(g[i].Length, expectedBufferSize);
                }

                Assert.Equal(g.Last().Length, expectedSizeOfLastBuffer);
            }

            [Fact]
            public void WhenBlockAlignmentIsOverCapacity_Throws_InvalidMemoryOperationException()
            {
                this.MemoryAllocator.BufferCapacityInBytes = 84; // 42 * Int16

                Assert.Throws<InvalidMemoryOperationException>(() =>
                {
                    MemoryGroup<short>.Allocate(this.MemoryAllocator, 50, 43);
                });
            }

            [Theory]
            [InlineData(AllocationOptions.None)]
            [InlineData(AllocationOptions.Clean)]
            public void MemoryAllocatorIsUtilizedCorrectly(AllocationOptions allocationOptions)
            {
                this.MemoryAllocator.BufferCapacityInBytes = 200;

                HashSet<int> bufferHashes;

                int expectedBlockCount = 5;
                using (var g = MemoryGroup<short>.Allocate(this.MemoryAllocator, 500, 100, allocationOptions))
                {
                    IReadOnlyList<TestMemoryAllocator.AllocationRequest> allocationLog = this.MemoryAllocator.AllocationLog;
                    Assert.Equal(expectedBlockCount, allocationLog.Count);
                    bufferHashes = allocationLog.Select(l => l.HashCodeOfBuffer).ToHashSet();
                    Assert.Equal(expectedBlockCount, bufferHashes.Count);
                    Assert.Equal(0, this.MemoryAllocator.ReturnLog.Count);

                    for (int i = 0; i < expectedBlockCount; i++)
                    {
                        Assert.Equal(allocationOptions, allocationLog[i].AllocationOptions);
                        Assert.Equal(100, allocationLog[i].Length);
                        Assert.Equal(200, allocationLog[i].LengthInBytes);
                    }
                }

                Assert.Equal(expectedBlockCount, this.MemoryAllocator.ReturnLog.Count);
                Assert.True(bufferHashes.SetEquals(this.MemoryAllocator.ReturnLog.Select(l => l.HashCodeOfBuffer)));
            }

            [Theory]
            [InlineData(128)]
            [InlineData(1024)]
            public void Allocate_OptionsContiguous_AllocatesContiguousBuffer(int lengthInBytes)
            {
                this.MemoryAllocator.BufferCapacityInBytes = 256;
                int length = lengthInBytes / Unsafe.SizeOf<S4>();
                using var g = MemoryGroup<S4>.Allocate(this.MemoryAllocator, length, 32, AllocationOptions.Contiguous);
                Assert.Equal(length, g.BufferLength);
                Assert.Equal(length, g.TotalLength);
                Assert.Equal(1, g.Count);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 5)]
    internal struct S5
    {
        public override string ToString() => "S5";
    }

    [StructLayout(LayoutKind.Sequential, Size = 4)]
    internal struct S4
    {
        public override string ToString() => "S4";
    }
}
