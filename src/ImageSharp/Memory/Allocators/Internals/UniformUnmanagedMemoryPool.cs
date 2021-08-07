﻿// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Threading;

namespace SixLabors.ImageSharp.Memory.Internals
{
    internal partial class UniformUnmanagedMemoryPool
    {
        private static readonly Stopwatch Stopwatch = Stopwatch.StartNew();

        private readonly TrimSettings trimSettings;
        private UnmanagedMemoryHandle[] buffers;
        private int index;
        private Timer trimTimer;
        private long lastTrimTimestamp;

        public UniformUnmanagedMemoryPool(int bufferLength, int capacity)
            : this(bufferLength, capacity, TrimSettings.Default)
        {
        }

        public UniformUnmanagedMemoryPool(int bufferLength, int capacity, TrimSettings trimSettings)
        {
            this.trimSettings = trimSettings;
            this.Capacity = capacity;
            this.BufferLength = bufferLength;
            this.buffers = new UnmanagedMemoryHandle[capacity];

            if (trimSettings.Enabled)
            {
                // Invoke the timer callback more frequently, than trimSettings.TrimPeriodMilliseconds,
                // and also invoke it on Gen 2 GC.
                // We are checking in the callback if enough time passed since the last trimming. If not, we do nothing.
                this.trimTimer = new Timer(
                    s => ((UniformUnmanagedMemoryPool)s)?.Trim(),
                    this,
                    this.trimSettings.TrimPeriodMilliseconds / 4,
                    this.trimSettings.TrimPeriodMilliseconds / 4);

#if NETCORE31COMPATIBLE
                Gen2GcCallback.Register(s => ((UniformUnmanagedMemoryPool)s).Trim(), this);
#endif
            }
        }

        public int BufferLength { get; }

        public int Capacity { get; }

        public UnmanagedMemoryHandle Rent(AllocationOptions allocationOptions = AllocationOptions.None)
        {
            UnmanagedMemoryHandle[] buffersLocal = this.buffers;

            // Avoid taking the lock if the pool is released or is over limit:
            if (buffersLocal == null || this.index == buffersLocal.Length)
            {
                return null;
            }

            UnmanagedMemoryHandle array;

            lock (buffersLocal)
            {
                // Check again after taking the lock:
                if (this.buffers == null || this.index == buffersLocal.Length)
                {
                    return null;
                }

                array = buffersLocal[this.index];
                buffersLocal[this.index++] = null;
            }

            if (array == null)
            {
                array = new UnmanagedMemoryHandle(this.BufferLength);
            }

            if (allocationOptions.Has(AllocationOptions.Clean))
            {
                this.GetSpan(array).Clear();
            }

            return array;
        }

        public UnmanagedMemoryHandle[] Rent(int bufferCount, AllocationOptions allocationOptions = AllocationOptions.None)
        {
            UnmanagedMemoryHandle[] buffersLocal = this.buffers;

            // Avoid taking the lock if the pool is released or is over limit:
            if (buffersLocal == null || this.index + bufferCount >= buffersLocal.Length + 1)
            {
                return null;
            }

            UnmanagedMemoryHandle[] result;
            lock (buffersLocal)
            {
                // Check again after taking the lock:
                if (this.buffers == null || this.index + bufferCount >= buffersLocal.Length + 1)
                {
                    return null;
                }

                result = new UnmanagedMemoryHandle[bufferCount];
                for (int i = 0; i < bufferCount; i++)
                {
                    result[i] = buffersLocal[this.index];
                    buffersLocal[this.index++] = null;
                }
            }

            for (int i = 0; i < result.Length; i++)
            {
                if (result[i] == null)
                {
                    result[i] = new UnmanagedMemoryHandle(this.BufferLength);
                }

                if (allocationOptions.Has(AllocationOptions.Clean))
                {
                    this.GetSpan(result[i]).Clear();
                }
            }

            return result;
        }

        public void Return(UnmanagedMemoryHandle buffer)
        {
            UnmanagedMemoryHandle[] buffersLocal = this.buffers;
            if (buffersLocal == null)
            {
                buffer.Dispose();
                return;
            }

            lock (buffersLocal)
            {
                // Check again after taking the lock:
                if (this.buffers == null)
                {
                    buffer.Dispose();
                    return;
                }

                if (this.index == 0)
                {
                    ThrowReturnedMoreArraysThanRented(); // DEBUG-only exception
                    buffer.Dispose();
                    return;
                }

                this.buffers[--this.index] = buffer;
            }
        }

        public void Return(Span<UnmanagedMemoryHandle> buffers)
        {
            UnmanagedMemoryHandle[] buffersLocal = this.buffers;
            if (buffersLocal == null)
            {
                DisposeAll(buffers);
                return;
            }

            lock (buffersLocal)
            {
                // Check again after taking the lock:
                if (this.buffers == null)
                {
                    DisposeAll(buffers);
                    return;
                }

                if (this.index - buffers.Length + 1 <= 0)
                {
                    ThrowReturnedMoreArraysThanRented();
                    DisposeAll(buffers);
                    return;
                }

                for (int i = buffers.Length - 1; i >= 0; i--)
                {
                    buffersLocal[--this.index] = buffers[i];
                }
            }
        }

        public void Release()
        {
            this.trimTimer?.Dispose();
            this.trimTimer = null;
            UnmanagedMemoryHandle[] oldBuffers = Interlocked.Exchange(ref this.buffers, null);
            DebugGuard.NotNull(oldBuffers, nameof(oldBuffers));
            DisposeAll(oldBuffers);
        }

        private static void DisposeAll(Span<UnmanagedMemoryHandle> buffers)
        {
            foreach (UnmanagedMemoryHandle handle in buffers)
            {
                handle?.Dispose();
            }
        }

        private unsafe Span<byte> GetSpan(UnmanagedMemoryHandle h) =>
            new Span<byte>((byte*)h.DangerousGetHandle(), this.BufferLength);

        // This indicates a bug in the library, however Return() might be called from a finalizer,
        // therefore we should never throw here in production.
        [Conditional("DEBUG")]
        private static void ThrowReturnedMoreArraysThanRented() =>
            throw new InvalidMemoryOperationException("Returned more arrays then rented");

        private bool Trim()
        {
            UnmanagedMemoryHandle[] buffersLocal = this.buffers;
            if (buffersLocal == null)
            {
                return false;
            }

            bool isHighPressure = this.IsHighMemoryPressure();

            if (isHighPressure)
            {
                return this.TrimHighPressure(buffersLocal);
            }

            long millisecondsSinceLastTrim = Stopwatch.ElapsedMilliseconds - this.lastTrimTimestamp;
            if (millisecondsSinceLastTrim > this.trimSettings.TrimPeriodMilliseconds)
            {
                return this.TrimLowPressure(buffersLocal);
            }

            return true;
        }

        private bool TrimHighPressure(UnmanagedMemoryHandle[] buffersLocal)
        {
            lock (buffersLocal)
            {
                if (this.buffers == null)
                {
                    return false;
                }

                // Trim all:
                for (int i = this.index; i < buffersLocal.Length && buffersLocal[i] != null; i++)
                {
                    buffersLocal[i] = null;
                }
            }

            return true;
        }

        private bool TrimLowPressure(UnmanagedMemoryHandle[] arraysLocal)
        {
            lock (arraysLocal)
            {
                if (this.buffers == null)
                {
                    return false;
                }

                // Count the arrays in the pool:
                int retainedCount = 0;
                for (int i = this.index; i < arraysLocal.Length && arraysLocal[i] != null; i++)
                {
                    retainedCount++;
                }

                // Trim 'trimRate' of 'retainedCount':
                int trimCount = (int)Math.Ceiling(retainedCount * this.trimSettings.Rate);
                int trimStart = this.index + retainedCount - 1;
                int trimStop = this.index + retainedCount - trimCount;
                for (int i = trimStart; i >= trimStop; i--)
                {
                    arraysLocal[i].Dispose();
                    arraysLocal[i] = null;
                }

                this.lastTrimTimestamp = Stopwatch.ElapsedMilliseconds;
            }

            return true;
        }

        private bool IsHighMemoryPressure()
        {
#if NETCORE31COMPATIBLE
            GCMemoryInfo memoryInfo = GC.GetGCMemoryInfo();
            return memoryInfo.MemoryLoadBytes >= memoryInfo.HighMemoryLoadThresholdBytes * this.trimSettings.HighPressureThresholdRate;
#else
            // We don't have high pressure detection triggering full trimming on other platforms,
            // to counterpart this, the maximum pool size is small.
            return false;
#endif
        }

        public class TrimSettings
        {
            // Trim half of the retained pool buffers every minute.
            public int TrimPeriodMilliseconds { get; set; } = 60_000;

            public float Rate { get; set; } = 0.5f;

            // Be more strict about high pressure threshold than ArrayPool<T>.Shared.
            // A 32 bit process can OOM before reaching HighMemoryLoadThresholdBytes.
            public float HighPressureThresholdRate { get; set; } = 0.5f;

            public bool Enabled => this.Rate > 0;

            public static TrimSettings Default => new TrimSettings();
        }
    }
}
