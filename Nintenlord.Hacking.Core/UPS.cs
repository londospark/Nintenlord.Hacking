using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nintenlord.Hacking.Core
{
    public class UPSfile : ICloneable
    {
        private readonly bool validPatch;
        public bool ValidPatch => validPatch;
        private uint originalFileCRC32;
        private uint newFileCRC32;
        private uint patchCRC32;
        private readonly ulong oldFileSize;
        private readonly ulong newFileSize;
        private readonly ulong[] changedOffsets = Array.Empty<ulong>();
        private readonly byte[][] XORbytes = Array.Empty<byte[]>();

        /// <summary>
        /// Creates a new UPS patch from a UPS file path.
        /// </summary>
        public UPSfile(string filePath)
            : this(File.Exists(filePath) ? ReadPatchFileBytes(filePath) : Array.Empty<byte>())
        {
        }

        private static byte[] ReadPatchFileBytes(string filePath)
        {
            using var br = new BinaryReader(File.OpenRead(filePath));
            return br.ReadBytes((int)br.BaseStream.Length);
        }

        /// <summary>
        /// Creates a new UPS patch from raw patch bytes (e.g. from a stream).
        /// </summary>
        public unsafe UPSfile(byte[] patchData)
        {
            var changedOffsetsList = new List<ulong>();
            var XORbytesList = new List<byte[]>();

            validPatch = false;

            if (patchData.Length < 16)
                return;

            fixed (byte* UPSptr = &patchData[0])
            {
                //header
                var currentPtr = UPSptr;
                var header = new string((sbyte*)currentPtr, 0, 4, Encoding.ASCII);
                if (header != "UPS1")
                    return;
                currentPtr += 4;
                oldFileSize = Decrypt(&currentPtr);
                newFileSize = Decrypt(&currentPtr);

                //body
                ulong filePosition = 0;
                while (currentPtr - UPSptr + 1 < patchData.Length - 12)
                {
                    filePosition += Decrypt(&currentPtr);
                    changedOffsetsList.Add(filePosition);
                    var newXORdata = new List<byte>();

                    while (*currentPtr != 0)
                    {
                        newXORdata.Add(*currentPtr++);
                    }
                    XORbytesList.Add(newXORdata.ToArray());
                    filePosition += (ulong)newXORdata.Count + 1;
                    currentPtr++;
                }

                //end
                originalFileCRC32 = *(uint*)currentPtr;
                newFileCRC32 = *(uint*)(currentPtr + 4);
                patchCRC32 = *(uint*)(currentPtr + 8);
            }

            changedOffsets = changedOffsetsList.ToArray();
            XORbytes = XORbytesList.ToArray();

            if (patchCRC32 != CalculatePatchCRC32())
                return;

            validPatch = true;
        }

        public UPSfile(byte[] originalFile, byte[] newFile)
        {
            var changedOffsetsList = new List<ulong>();
            var XORbytesList = new List<byte[]>();
            validPatch = true;
            oldFileSize = (ulong)originalFile.Length;
            newFileSize = (ulong)newFile.Length;

            ulong maxSize;
            if (oldFileSize > newFileSize)
                maxSize = oldFileSize;
            else
                maxSize = newFileSize;

            for (ulong i = 0; i < maxSize; i++)
            {
                var x = i < oldFileSize ? originalFile[i] : (byte)0x00;
                var y = i < newFileSize ? newFile[i] : (byte)0x00;

                if (x != y)
                {
                    changedOffsetsList.Add(i);
                    var newXORbytes = new List<byte>();
                    while (x != y && i < maxSize)
                    {
                        newXORbytes.Add((byte)(x ^ y));
                        i++;
                        x = i < oldFileSize ? originalFile[i] : (byte)0x00;
                        y = i < newFileSize ? newFile[i] : (byte)0x00;
                    }
                    XORbytesList.Add(newXORbytes.ToArray());
                }
            }
            originalFileCRC32 = CRC32.CalculateCRC32(originalFile);
            newFileCRC32 = CRC32.CalculateCRC32(newFile);
            changedOffsets = changedOffsetsList.ToArray();
            XORbytes = XORbytesList.ToArray();
            patchCRC32 = CalculatePatchCRC32();
        }

        private UPSfile(ulong[] changedOffsets, byte[][] XORbytes, uint originalFileCRC32, uint newFileCRC32, ulong oldFileSize, ulong newFileSize)
        {
            this.changedOffsets = (ulong[])changedOffsets.Clone();
            this.XORbytes = new byte[XORbytes.Length][];
            for (var i = 0; i < this.XORbytes.Length; i++)
            {
                this.XORbytes[i] = (byte[])XORbytes[i].Clone();
            }
            this.originalFileCRC32 = originalFileCRC32;
            this.newFileCRC32 = newFileCRC32;
            this.oldFileSize = oldFileSize;
            this.newFileSize = newFileSize;
            patchCRC32 = CalculatePatchCRC32();
        }

        private static byte[] Encrypt(ulong offset)
        {
            var bytes = new List<byte>(8);

            var x = offset & 0x7f;
            offset >>= 7;
            while (offset != 0)
            {
                bytes.Add((byte)x);
                offset--;
                x = offset & 0x7f;
                offset >>= 7;
            }
            bytes.Add((byte)(0x80 | x));
            return bytes.ToArray();
        }

        private static unsafe ulong Decrypt(byte** pointer)
        {
            ulong value = 0;
            var shift = 1;
            var x = *(*pointer)++;
            value += (ulong)((x & 0x7F) * shift);
            while ((x & 0x80) == 0)
            {
                shift <<= 7;
                value += (ulong)shift;
                x = *(*pointer)++;
                value += (ulong)((x & 0x7F) * shift);
            }
            return value;
        }

        private uint CalculatePatchCRC32() => CRC32.CalculateCRC32(ToBinary());

        public bool ValidToApply(byte[] file)
        {
            var fileCRC32 = CRC32.CalculateCRC32(file);
            var fitsAsOld = oldFileSize == (ulong)file.Length && fileCRC32 == originalFileCRC32;
            var fitsAsNew = newFileSize == (ulong)file.Length && fileCRC32 == newFileCRC32;

            return validPatch && (fitsAsOld || fitsAsNew);
        }

        public async Task<bool> ValidToApplyAsync(string path, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
        {
            if (!File.Exists(path))
                return false;

            var fileSize = (ulong)new FileInfo(path).Length;

            // Skip CRC if size already doesn't match either expected size.
            if (fileSize != oldFileSize && fileSize != newFileSize)
                return false;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var fileCRC32 = await CRC32.CalculateCRC32Async(stream, cancellationToken, progress).ConfigureAwait(false);

            var fitsAsOld = fileSize == oldFileSize && fileCRC32 == originalFileCRC32;
            var fitsAsNew = fileSize == newFileSize && fileCRC32 == newFileCRC32;

            return validPatch && (fitsAsOld || fitsAsNew);
        }

        /// <summary>
        /// Applies the patch by streaming input to output without loading the entire ROM into memory.
        /// Writes to a temporary file beside <paramref name="outputPath"/> and atomically replaces it on success.
        /// Reports progress as a percentage (0–100).
        /// </summary>
        public async Task ApplyAsync(
            string inputPath,
            string outputPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var tempPath = outputPath + ".patching.tmp";
            try
            {
                await ApplyStreamAsync(inputPath, tempPath, progress, cancellationToken).ConfigureAwait(false);

                if (File.Exists(outputPath))
                    File.Delete(outputPath);
                File.Move(tempPath, outputPath);
            }
            catch
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
                throw;
            }
        }

        private async Task ApplyStreamAsync(
            string inputPath,
            string outputPath,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            const int bufferSize = 81920;
            var copyBuffer = new byte[bufferSize];
            var totalBytes = (double)newFileSize;

            using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize, FileOptions.Asynchronous);

            long position = 0;
            var lastReportedPct = -1;

            for (var patchIndex = 0; patchIndex <= changedOffsets.Length; patchIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var nextPatchOffset = patchIndex < changedOffsets.Length
                    ? (long)changedOffsets[patchIndex]
                    : (long)newFileSize;

                // Copy (or zero-pad) unchanged bytes from current position up to the next patch.
                var toCopy = nextPatchOffset - position;
                while (toCopy > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var chunkSize = (int)Math.Min(toCopy, copyBuffer.Length);
                    var bytesRead = 0;

                    if (input.Position < input.Length)
                    {
                        var available = (int)Math.Min(chunkSize, input.Length - input.Position);
                        bytesRead = await ReadExactAsync(input, copyBuffer, 0, available, cancellationToken).ConfigureAwait(false);
                    }

                    // Zero-pad if input is shorter than the new file size.
                    if (bytesRead < chunkSize)
                        Array.Clear(copyBuffer, bytesRead, chunkSize - bytesRead);

                    await output.WriteAsync(copyBuffer, 0, chunkSize, cancellationToken).ConfigureAwait(false);
                    position += chunkSize;
                    toCopy -= chunkSize;

                    ReportThrottled(progress, position, totalBytes, ref lastReportedPct);
                }

                if (patchIndex >= changedOffsets.Length)
                    break;

                // Apply the XOR patch run at this offset.
                var xorBytes = XORbytes[patchIndex];
                var patchBuffer = new byte[xorBytes.Length];

                if (input.Position < input.Length)
                {
                    var available = (int)Math.Min(xorBytes.Length, input.Length - input.Position);
                    await ReadExactAsync(input, patchBuffer, 0, available, cancellationToken).ConfigureAwait(false);
                    // Bytes beyond EOF are already 0x00 — XOR with patch byte = patch byte, which is correct.
                }

                XorInto(patchBuffer.AsSpan(), xorBytes);

                await output.WriteAsync(patchBuffer, 0, patchBuffer.Length, cancellationToken).ConfigureAwait(false);
                position += xorBytes.Length;

                ReportThrottled(progress, position, totalBytes, ref lastReportedPct);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(100.0);
        }

        /// <summary>
        /// Reports progress only when the integer percentage changes, capping UI callbacks at ~101
        /// instead of firing every 80 KB (which would mean ~18,000 Dispatcher.Post calls for a 1.5 GB ISO).
        /// </summary>
        private static void ReportThrottled(IProgress<double>? progress, long position, double totalBytes, ref int lastReportedPct)
        {
            if (progress == null) return;
            var pct = (int)(position / totalBytes * 100.0);
            if (pct != lastReportedPct)
            {
                progress.Report(pct);
                lastReportedPct = pct;
            }
        }

        private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                var bytesRead = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                    break;
                totalRead += bytesRead;
            }
            return totalRead;
        }

        /// <summary>
        /// XORs each byte of <paramref name="target"/> with the corresponding byte of <paramref name="source"/>
        /// using portable SIMD (Vector&lt;byte&gt;) when hardware acceleration is available.
        /// </summary>
        private static void XorInto(Span<byte> target, ReadOnlySpan<byte> source)
        {
            var i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var vecTarget = MemoryMarshal.Cast<byte, Vector<byte>>(target);
                var vecSource = MemoryMarshal.Cast<byte, Vector<byte>>(source);
                for (var v = 0; v < vecTarget.Length; v++)
                    vecTarget[v] ^= vecSource[v];
                i = vecTarget.Length * Vector<byte>.Count;
            }
            for (; i < target.Length; i++)
                target[i] ^= source[i];
        }

        public unsafe byte[] Apply(byte[] file)
        {
            var lenght = (ulong)file.LongLength;
            if (lenght < newFileSize)
                lenght = newFileSize;

            var result = new byte[lenght];

            fixed (byte* resultPtr = &result[0])
            {
                Marshal.Copy(file, 0, new IntPtr(resultPtr), Math.Min(file.Length, result.Length));
            }

            for (var i = 0; i < changedOffsets.LongLength; i++)
                XorInto(result.AsSpan((int)changedOffsets[i], XORbytes[i].Length), XORbytes[i]);

            return result;
        }

        public byte[]? Apply(string path)
        {
            if (!validPatch || !File.Exists(path))
                return null;

            var br = new BinaryReader(File.Open(path, FileMode.Open));
            var file = br.ReadBytes((int)br.BaseStream.Length);
            br.Close();
            return Apply(file);
        }

        private byte[] ToBinary()
        {
            var file = new List<byte>();
            file.Add((byte)'U');
            file.Add((byte)'P');
            file.Add((byte)'S');
            file.Add((byte)'1');
            file.AddRange(Encrypt(oldFileSize));
            file.AddRange(Encrypt(newFileSize));

            for (var i = 0; i < changedOffsets.LongLength; i++)
            {
                var relativeOffset = changedOffsets[i];
                if (i != 0)
                    relativeOffset -= changedOffsets[i - 1] + (ulong)XORbytes[i - 1].Length + 1;

                file.AddRange(Encrypt(relativeOffset));
                file.AddRange(XORbytes[i]);
                file.Add(0);
            }

            file.AddRange(BitConverter.GetBytes(originalFileCRC32));
            file.AddRange(BitConverter.GetBytes(newFileCRC32));

            return file.ToArray();
        }

        public void WriteToFile(string path)
        {
            var bw = new BinaryWriter(File.Open(path, FileMode.Create));
            var file = ToBinary();
            bw.Write(file);
            bw.Write(CRC32.CalculateCRC32(file));
            bw.Close();
        }

        public int[,] GetData()
        {
            var result = new int[changedOffsets.Length, 2];
            for (var i = 0; i < changedOffsets.Length; i++)
            {
                result[i, 0] = (int)changedOffsets[i];
                result[i, 1] = XORbytes[i].Length;
            }
            return result;
        }

        public bool ChangesOffset(ulong offset)
        {
            for (var i = 0; changedOffsets[i] <= offset && i < changedOffsets.Length; i++)
            {
                if (changedOffsets[i] <= offset && offset < changedOffsets[i] + (ulong)XORbytes[i].Length)
                    return true;
            }
            return false;
        }

        public bool ChangeOffsets(ulong offset, int length)
        {
            for (var i = 0; changedOffsets[i] <= offset + (ulong)length && i < changedOffsets.Length; i++)
            {
                if (changedOffsets[i] <= offset && changedOffsets[i] + (ulong)XORbytes[i].LongLength > offset)
                    return true;
                else if (changedOffsets[i] <= offset + (ulong)length && offset + (ulong)length < changedOffsets[i] + (ulong)XORbytes[i].Length)
                    return true;
            }
            return false;
        }

        public static UPSfile operator +(UPSfile a, UPSfile b)
        {
            var emptyFile = new byte[Math.Max(a.newFileSize, b.newFileSize)];
            var OrigEmptyFile = (byte[])emptyFile.Clone();
            emptyFile = b.Apply(a.Apply(emptyFile));

            var result = new UPSfile(OrigEmptyFile, emptyFile);
            result.patchCRC32 = result.CalculatePatchCRC32();
            result.originalFileCRC32 = a.originalFileCRC32;
            result.newFileCRC32 = 0;

            return result;
        }

        #region ICloneable Members
        /// <summary>
        /// Creates a deeb copy of the object
        /// </summary>
        /// <returns>A deeb copy of the object</returns>
        public object Clone() => new UPSfile(changedOffsets, XORbytes, originalFileCRC32, newFileCRC32, oldFileSize, newFileSize);

        #endregion
    }

}
