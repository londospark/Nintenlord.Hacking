using System;
using Nintenlord.MemoryManagement;

namespace Nintenlord.Hacking.Core.MemoryManagement
{
    public sealed class ManagedPointer : IComparable<ManagedPointer>, IEquatable<ManagedPointer>, IMemoryPointer
    {
        private int offset;
        private readonly int size;
        private readonly bool pinned;

        public int Offset
        {
            get => offset;
            internal set
            {
                if (!pinned)
                {
                    throw new NotSupportedException("Pointers referance can't be moved.");
                }

                offset = value;
            }
        }
        public int Size => size;

        public bool Pinned => pinned;

        public int OffsetAfter => offset + size;

        public bool IsNull => offset == -1;

        static public readonly ManagedPointer NullPointer = new ManagedPointer();

        private ManagedPointer()
        {
            offset = -1;
            size = 0;
            pinned = false;
        }

        internal ManagedPointer(int offset, int size, bool pinned)
        {
            if (size < 0)
            {
                throw new ArgumentException("Size is negative.");
            }
            this.offset = offset;
            this.size = size;
            this.pinned = pinned;
        }

        private ManagedPointer(ManagedPointer copyMe)
        {
            offset = copyMe.offset;
            size = copyMe.size;
            pinned = copyMe.pinned;
        }

        #region IComparable<ManagedPointer> Members

        public int CompareTo(ManagedPointer? other)
        {
            if (other is null) return 1;
            return offset - other.offset;
        }

        #endregion

        #region IEquatable<ManagedPointer> Members

        public bool Equals(ManagedPointer? other) =>
            other is not null &&
            size == other.size &&
            offset == other.offset;

        #endregion

        public override bool Equals(object? obj)
        {
            if (obj is ManagedPointer)
            {
                return Equals((ManagedPointer)obj);
            }
            else
            {
                return false;
            }
        }

        public override int GetHashCode() => offset;

        public override string ToString() => string.Format("{{Offset: ${0} Size: 0x{1}}}", offset.ToString("X6"), size.ToString("X"));
    }
}
