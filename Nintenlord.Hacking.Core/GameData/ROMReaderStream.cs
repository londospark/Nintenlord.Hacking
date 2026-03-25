using System;
using System.IO;

namespace Nintenlord.Hacking.Core.GameData
{
    public sealed class ROMReaderStream : Stream
    {
        private readonly IROM rom;
        private long position;

        public ROMReaderStream(IROM romToRead)
        {
            rom = romToRead;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override void Flush()
        {
            //No OP
        }

        public override long Length => rom.Length;

        public override long Position
        {
            get => position;
            set => position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var data = rom.ReadData((int)position, count);

            Array.Copy(data, 0, buffer, offset, data.Length);
            position += count;
            return data.Length;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    position = offset;
                    break;
                case SeekOrigin.Current:
                    position += offset;
                    break;
                case SeekOrigin.End:
                    position = rom.Length + offset;
                    break;
                default:
                    break;
            }
            return position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
