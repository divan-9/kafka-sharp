﻿// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0

using System;
using System.IO;
using System.Threading;
using Kafka.Public;

namespace Kafka.Common
{
    /// <summary>
    /// Implement a pool of MemoryStream allowing for recycling
    /// of underlying buffers. This kills two birds with one stone:
    /// we can minimize MemoryStream/buffers creation when (de)serialing requests/responses
    /// and we can minimize the number of buffers passed to the network layers.
    ///
    /// TODO: (for all pools) check that we do not keep too many objects in the pool
    /// </summary>
    class ReusableMemoryStream : MemoryStream, IMemorySerializable, IDisposable
    {
        private static readonly Pool<ReusableMemoryStream> ChunkPool = new Pool<ReusableMemoryStream>(() =>  new ReusableMemoryStream(ChunkPool), s => s.SetLength(0));
        private static readonly Pool<ReusableMemoryStream> BatchPool = new Pool<ReusableMemoryStream>(() => new ReusableMemoryStream(BatchPool), s => s.SetLength(0));
        internal const int SmallLimit = 81920; // 20 * 4096, just under LOH limit

        private static int _nextId;
        private readonly int _id; // Useful to track leaks while debugging
        private readonly Pool<ReusableMemoryStream> _myPool;

        private ReusableMemoryStream(Pool<ReusableMemoryStream> myPool)
        {
            _id = Interlocked.Increment(ref _nextId);
            _myPool = myPool;
        }

        // Used for single message serialization and "small" buffers.
        public static ReusableMemoryStream Reserve()
        {
            return ChunkPool.Reserve();
        }

        // Used for messageset serialization and "big" responses.
        public static ReusableMemoryStream ReserveBatch()
        {
            return BatchPool.Reserve();
        }

        // Route to Reserve()/ReserveBatch depending on requested size
        public static ReusableMemoryStream Reserve(int capacity)
        {
            var rms = capacity > SmallLimit ? BatchPool.Reserve() : ChunkPool.Reserve();
            return rms.EnsureCapacity(capacity);
        }

        private static void Release(ReusableMemoryStream stream)
        {
            stream._myPool.Release(stream);
        }

        internal byte this[int index]
        {
            get { return GetBuffer()[index]; }
            set { GetBuffer()[index] = value; }
        }

        public new void Dispose()
        {
            (this as IDisposable).Dispose();
        }

        void IDisposable.Dispose()
        {
            Release(this);
        }

        private ReusableMemoryStream EnsureCapacity(int capacity)
        {
            int position = (int) Position;
            SetLength(capacity);
            Position = position;
            return this;
        }

        public void Serialize(MemoryStream toStream)
        {
            byte[] array = GetBuffer();
            int length = (int)Length;
            toStream.Write(array, 0, length);
        }
    }

    static class ReusableExtentions
    {
        // CopyTo allocates a temporary buffer. As we're already pooling buffers,
        // we might as well provide a CopyTo that makes use of that instead of
        // using Stream.CopyTo (which allocates a buffer of its own, even if it
        // does so efficiently).
        public static void ReusableCopyTo(this Stream input, Stream destination)
        {
            using (var m = ReusableMemoryStream.Reserve(ReusableMemoryStream.SmallLimit))
            {
                var buffer = m.GetBuffer();
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    destination.Write(buffer, 0, read);
                }
            }
        }
    }
}