using System;
using System.IO;
using Nintenlord.Hacking.Core.MemoryManagement;

namespace Nintenlord.Hacking.Core.GameData
{
    public sealed class ROMWriterStream : Stream
    {
        private readonly IROM rom;
        private readonly ManagedPointer areaToWriteTo;
        private long offset;
        private readonly byte[] buffer;

        public ROMWriterStream(IROM rom, ManagedPointer areaToWriteTo)
        {
            this.rom = rom;
            this.areaToWriteTo = areaToWriteTo;
            offset = areaToWriteTo.Offset;
            buffer = rom.ReadData(areaToWriteTo);
        }

        public override bool CanRead => false;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override void Flush() => rom.WriteData(areaToWriteTo, buffer, 0, buffer.Length);

        public override long Length => rom.Length;

        public override long Position
        {
            get => offset;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            long positionAfter;
            switch (origin)
            {
                case SeekOrigin.Begin:
                    positionAfter = offset;
                    break;
                case SeekOrigin.Current:
                    positionAfter = this.offset + offset;
                    break;
                case SeekOrigin.End:
                    positionAfter = rom.Length + offset;
                    break;
                default:
                    throw new ArgumentException("not valid option", "origin");
            }
            if (positionAfter < areaToWriteTo.Offset || positionAfter >= areaToWriteTo.OffsetAfter)
            {
                throw new IOException("Offset out of range");
            }
            this.offset = positionAfter;
            return positionAfter;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Array.Copy(buffer, offset, buffer, offset - areaToWriteTo.Offset, count);
            offset += count;
        }
    }
}
