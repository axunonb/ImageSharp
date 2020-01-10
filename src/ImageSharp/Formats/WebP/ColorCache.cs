// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

namespace SixLabors.ImageSharp.Formats.WebP
{
    internal class ColorCache
    {
        private const uint KHashMul = 0x1e35a7bdu;

        /// <summary>
        /// Gets the color entries.
        /// </summary>
        public uint[] Colors { get; private set; }

        /// <summary>
        /// Gets the hash shift: 32 - hashBits.
        /// </summary>
        public int HashShift { get; private set; }

        /// <summary>
        /// Gets the hash bits.
        /// </summary>
        public int HashBits { get; private set; }

        public void Init(int hashBits)
        {
            int hashSize = 1 << hashBits;
            this.Colors = new uint[hashSize];
            this.HashBits = hashBits;
            this.HashShift = 32 - hashBits;
        }

        public void Insert(uint argb)
        {
            int key = this.HashPix(argb, this.HashShift);
            this.Colors[key] = argb;
        }

        public uint Lookup(int key)
        {
            return this.Colors[key];
        }

        private int HashPix(uint argb, int shift)
        {
            return (int)((argb * KHashMul) >> shift);
        }
    }
}
