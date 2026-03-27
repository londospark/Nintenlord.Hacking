using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HardwareCrc32 = System.IO.Hashing.Crc32;

namespace Nintenlord.Hacking.Core
{
    public class UPSfile : ICloneable, IDisposable
    {
        private readonly bool validPatch;
        public bool ValidPatch => validPatch;
        private uint originalFileCRC32;
        private uint newFileCRC32;
        private uint patchCRC32;
        private readonly ulong oldFileSize;
        private readonly ulong newFileSize;
        private readonly ulong[] changedOffsets = Array.Empty<ulong>();

        // Indices into the patch data for each XOR run (set on all load paths).
        private readonly int[] _patchRunOffsets = Array.Empty<int>();
        private readonly int[] _patchRunLengths = Array.Empty<int>();

        // Memory-mapped path (UPSfile(string)): zero-copy, OS pages on demand.
        // Works on Windows, macOS, Linux. Falls back to byte[] on iOS/Android
        // where file-picker returns a URI without a local path.
        private readonly MemoryMappedFile? _mappedFile;
        private readonly MemoryMappedViewAccessor? _mappedViewAccessor;
        private nint _mappedViewPointer;   // byte* as nint; 0 when not memory-mapped
        private readonly long _mappedFileLength;

        // Byte-array path (UPSfile(byte[])): used when only a stream is available
        // (e.g. IStorageFile on mobile). Stored once; runs slice into it, no copy.
        private readonly byte[]? _patchData;

        // Create path (UPSfile(byte[] original, byte[] newFile)): XOR bytes built
        // in memory. _patchData and _mappedViewPointer remain null/0.
        private readonly byte[][] XORbytes = Array.Empty<byte[]>();

        /// <summary>Returns a read-only view of the XOR bytes for patch run <paramref name="i"/>.</summary>
        private unsafe ReadOnlySpan<byte> GetXorRun(int i) =>
            _mappedViewPointer != 0
                ? new ReadOnlySpan<byte>((byte*)_mappedViewPointer + _patchRunOffsets[i], _patchRunLengths[i])
                : _patchData != null
                    ? _patchData.AsSpan(_patchRunOffsets[i], _patchRunLengths[i])
                    : XORbytes[i].AsSpan();

        private int GetXorRunLength(int i) =>
            _mappedViewPointer != 0 || _patchData != null
                ? _patchRunLengths[i]
                : XORbytes[i].Length;

        /// <summary>
        /// Loads a UPS patch from a file path using a memory-mapped view.
        /// The OS pages in only the regions actually accessed; no full copy is made.
        /// Dispose this instance when patching is complete to release the mapping.
        /// </summary>
        public unsafe UPSfile(string filePath)
        {
            validPatch = false;
            if (!File.Exists(filePath)) return;

            _mappedFileLength = new FileInfo(filePath).Length;
            if (_mappedFileLength < 16) return;

            _mappedFile = MemoryMappedFile.CreateFromFile(
                filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _mappedViewAccessor = _mappedFile.CreateViewAccessor(
                0, 0, MemoryMappedFileAccess.Read);

            byte* ptr = null;
            _mappedViewAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            // PointerOffset accounts for page-alignment padding before byte 0 of the file.
            _mappedViewPointer = (nint)(ptr + _mappedViewAccessor.PointerOffset);

            if (!TryParsePatch((byte*)_mappedViewPointer, (int)_mappedFileLength,
                    out var offsets, out var runOffsets, out var runLengths,
                    out oldFileSize, out newFileSize,
                    out originalFileCRC32, out newFileCRC32, out patchCRC32))
                return;

            changedOffsets = offsets;
            _patchRunOffsets = runOffsets;
            _patchRunLengths = runLengths;

            if (patchCRC32 != CalculatePatchCRC32()) return;
            validPatch = true;
        }

        /// <summary>
        /// Loads a UPS patch from raw bytes already read into memory (e.g. from
        /// IStorageFile.OpenReadAsync() on mobile). The array is stored once;
        /// XOR run data is never duplicated — only offsets/lengths are recorded.
        /// </summary>
        public unsafe UPSfile(byte[] patchData)
        {
            validPatch = false;
            if (patchData.Length < 16) return;

            fixed (byte* ptr = &patchData[0])
            {
                if (!TryParsePatch(ptr, patchData.Length,
                        out var offsets, out var runOffsets, out var runLengths,
                        out oldFileSize, out newFileSize,
                        out originalFileCRC32, out newFileCRC32, out patchCRC32))
                    return;

                changedOffsets = offsets;
                _patchRunOffsets = runOffsets;
                _patchRunLengths = runLengths;
            }

            _patchData = patchData;
            if (patchCRC32 != CalculatePatchCRC32()) return;
            validPatch = true;
        }

        /// <summary>
        /// Parses the UPS binary format from a raw pointer. Used by both load
        /// constructors so the format-reading logic is not duplicated.
        /// </summary>
        private static unsafe bool TryParsePatch(
            byte* dataPtr, int dataLength,
            out ulong[] changedOffsets,
            out int[] patchRunOffsets,
            out int[] patchRunLengths,
            out ulong oldFileSize,
            out ulong newFileSize,
            out uint originalFileCRC32,
            out uint newFileCRC32,
            out uint patchCRC32)
        {
            changedOffsets = Array.Empty<ulong>();
            patchRunOffsets = Array.Empty<int>();
            patchRunLengths = Array.Empty<int>();
            oldFileSize = newFileSize = 0;
            originalFileCRC32 = newFileCRC32 = patchCRC32 = 0;

            if (dataLength < 16) return false;

            var currentPtr = dataPtr;
            var header = new string((sbyte*)currentPtr, 0, 4, Encoding.ASCII);
            if (header != "UPS1") return false;
            currentPtr += 4;
            oldFileSize = Decrypt(&currentPtr);
            newFileSize = Decrypt(&currentPtr);

            var offsetsList = new List<ulong>();
            var runOffsetsList = new List<int>();
            var runLengthsList = new List<int>();

            ulong filePosition = 0;
            while (currentPtr - dataPtr + 1 < dataLength - 12)
            {
                filePosition += Decrypt(&currentPtr);
                offsetsList.Add(filePosition);

                var runStart = (int)(currentPtr - dataPtr);
                var runLen = 0;
                while (*currentPtr != 0) { currentPtr++; runLen++; }
                runOffsetsList.Add(runStart);
                runLengthsList.Add(runLen);

                filePosition += (ulong)runLen + 1;
                currentPtr++; // skip null terminator
            }

            originalFileCRC32 = *(uint*)currentPtr;
            newFileCRC32 = *(uint*)(currentPtr + 4);
            patchCRC32 = *(uint*)(currentPtr + 8);

            changedOffsets = offsetsList.ToArray();
            patchRunOffsets = runOffsetsList.ToArray();
            patchRunLengths = runLengthsList.ToArray();
            return true;
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

        private unsafe uint CalculatePatchCRC32()
        {
            if (_mappedViewPointer != 0)
                return CRC32.CalculateCRC32(
                    new ReadOnlySpan<byte>((byte*)_mappedViewPointer, (int)(_mappedFileLength - 4)));
            if (_patchData != null)
                return CRC32.CalculateCRC32(_patchData, 0, _patchData.Length - 4);
            return CRC32.CalculateCRC32(ToBinary());
        }

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
                // Materialise the run to a byte array so it can cross the await boundaries below.
                var xorRun = GetXorRun(patchIndex).ToArray();
                var patchBuffer = new byte[xorRun.Length];

                if (input.Position < input.Length)
                {
                    var available = (int)Math.Min(xorRun.Length, input.Length - input.Position);
                    await ReadExactAsync(input, patchBuffer, 0, available, cancellationToken).ConfigureAwait(false);
                    // Bytes beyond EOF are already 0x00 — XOR with patch byte = patch byte, which is correct.
                }

                XorInto(patchBuffer.AsSpan(), xorRun);

                await output.WriteAsync(patchBuffer, 0, patchBuffer.Length, cancellationToken).ConfigureAwait(false);
                position += xorRun.Length;

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
                XorInto(result.AsSpan((int)changedOffsets[i], GetXorRunLength(i)), GetXorRun(i));

            return result;
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
                    relativeOffset -= changedOffsets[i - 1] + (ulong)GetXorRunLength(i - 1) + 1;

                file.AddRange(Encrypt(relativeOffset));
                foreach (var b in GetXorRun(i)) file.Add(b);
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
                result[i, 1] = GetXorRunLength(i);
            }
            return result;
        }

        public bool ChangesOffset(ulong offset)
        {
            for (var i = 0; i < changedOffsets.Length && changedOffsets[i] <= offset; i++)
            {
                if (offset < changedOffsets[i] + (ulong)GetXorRunLength(i))
                    return true;
            }
            return false;
        }

        public bool ChangeOffsets(ulong offset, int length)
        {
            // Standard interval overlap: run [runStart, runStart+runLen) ∩ range [offset, offset+length) ≠ ∅
            // iff runStart < offset+length AND runStart+runLen > offset
            for (var i = 0; i < changedOffsets.Length && changedOffsets[i] < offset + (ulong)length; i++)
            {
                if (changedOffsets[i] + (ulong)GetXorRunLength(i) > offset)
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
        /// Creates a deep copy of this patch. Works for all load paths (memory-mapped,
        /// byte-array and in-memory create) by materialising XOR run data via GetXorRun.
        /// </summary>
        public object Clone()
        {
            var xorData = new byte[changedOffsets.Length][];
            for (var i = 0; i < xorData.Length; i++)
                xorData[i] = GetXorRun(i).ToArray();
            return new UPSfile(changedOffsets, xorData, originalFileCRC32, newFileCRC32, oldFileSize, newFileSize);
        }
        #endregion

        #region Streaming create
        /// <summary>
        /// Creates a UPS patch file from two files on disk without loading either into RAM.
        /// Memory-maps both input files, computes CRC32 and the XOR diff in a single pass,
        /// and writes the patch incrementally to a temp file — atomically replacing
        /// <paramref name="outputPath"/> on success.
        /// </summary>
        public static Task WriteAsync(
            string originalPath,
            string modifiedPath,
            string outputPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.Run(() => WriteCore(originalPath, modifiedPath, outputPath, progress, cancellationToken),
                cancellationToken);

        private static unsafe void WriteCore(
            string originalPath,
            string modifiedPath,
            string outputPath,
            IProgress<double>? progress,
            CancellationToken ct)
        {
            var originalSize = (ulong)new FileInfo(originalPath).Length;
            var modifiedSize = (ulong)new FileInfo(modifiedPath).Length;
            var maxSize = Math.Max(originalSize, modifiedSize);

            var tempPath = outputPath + ".creating.tmp";
            try
            {
                using var origMapped = MemoryMappedFile.CreateFromFile(
                    originalPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                using var modiMapped = MemoryMappedFile.CreateFromFile(
                    modifiedPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                using var origAccess = origMapped.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                using var modiAccess = modiMapped.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

                byte* origPtr = null, modiPtr = null;
                try
                {
                    origAccess.SafeMemoryMappedViewHandle.AcquirePointer(ref origPtr);
                    modiAccess.SafeMemoryMappedViewHandle.AcquirePointer(ref modiPtr);
                    origPtr += origAccess.PointerOffset;
                    modiPtr += modiAccess.PointerOffset;

                    // CRC32 of both files — hardware-accelerated SIMD, very fast even for 2 GB.
                    var origCrc = CRC32.CalculateCRC32(new ReadOnlySpan<byte>(origPtr, (int)originalSize));
                    var modiCrc = CRC32.CalculateCRC32(new ReadOnlySpan<byte>(modiPtr, (int)modifiedSize));

                    var patchHash = new HardwareCrc32();

                    using var outStream = new FileStream(
                        tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

                    // ---- Header ----
                    var header = stackalloc byte[] { (byte)'U', (byte)'P', (byte)'S', (byte)'1' };
                    var headerSpan = new ReadOnlySpan<byte>(header, 4);
                    outStream.Write(headerSpan);
                    patchHash.Append(headerSpan);
                    WriteVarUIntCore(outStream, patchHash, originalSize);
                    WriteVarUIntCore(outStream, patchHash, modifiedSize);

                    // ---- Diff scan ----
                    ulong romCursor = 0;   // ROM cursor after previous run's null-terminator position
                    ulong filePos = 0;
                    var lastPct = -1;
                    var totalBytes = (double)maxSize;
                    var zero = stackalloc byte[1]; // null terminator written after each XOR run

                    while (filePos < maxSize)
                    {
                        // Check for cancellation and report progress every 64 KB.
                        if ((filePos & 0xFFFF) == 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            ReportThrottled(progress, (long)filePos, totalBytes, ref lastPct);
                        }

                        var x = filePos < originalSize ? origPtr[filePos] : (byte)0;
                        var y = filePos < modifiedSize ? modiPtr[filePos] : (byte)0;

                        if (x != y)
                        {
                            // Encode the relative offset from the romCursor to the start of this run.
                            WriteVarUIntCore(outStream, patchHash, filePos - romCursor);

                            // Collect and write the XOR run.
                            var runXor = new List<byte>(128);
                            while (filePos < maxSize)
                            {
                                x = filePos < originalSize ? origPtr[filePos] : (byte)0;
                                y = filePos < modifiedSize ? modiPtr[filePos] : (byte)0;
                                if (x == y) break;
                                runXor.Add((byte)(x ^ y));
                                filePos++;
                            }
                            var runBuf = runXor.ToArray();
                            outStream.Write(runBuf);
                            patchHash.Append(runBuf);

                            // Null terminator: the first matching byte after the run.
                            outStream.Write(new ReadOnlySpan<byte>(zero, 1));
                            patchHash.Append(new ReadOnlySpan<byte>(zero, 1));

                            filePos++;          // advance past the null terminator's ROM position
                            romCursor = filePos;
                        }
                        else
                        {
                            filePos++;
                        }
                    }

                    // ---- Footer ----
                    var origCrcBytes = BitConverter.GetBytes(origCrc);
                    var modiCrcBytes = BitConverter.GetBytes(modiCrc);
                    outStream.Write(origCrcBytes);
                    patchHash.Append(origCrcBytes);
                    outStream.Write(modiCrcBytes);
                    patchHash.Append(modiCrcBytes);
                    outStream.Write(BitConverter.GetBytes(patchHash.GetCurrentHashAsUInt32()));

                    outStream.Flush();
                    progress?.Report(100.0);
                }
                finally
                {
                    if (origPtr != null) origAccess.SafeMemoryMappedViewHandle.ReleasePointer();
                    if (modiPtr != null) modiAccess.SafeMemoryMappedViewHandle.ReleasePointer();
                }

                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(tempPath, outputPath);
            }
            catch
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
                throw;
            }
        }

        private static void WriteVarUIntCore(Stream stream, HardwareCrc32 hash, ulong value)
        {
            var encoded = Encrypt(value);
            stream.Write(encoded);
            hash.Append(encoded);
        }
        #endregion

        #region IDisposable Members
        public void Dispose()
        {
            if (_mappedViewPointer != 0)
            {
                _mappedViewAccessor!.SafeMemoryMappedViewHandle.ReleasePointer();
                _mappedViewPointer = 0;
            }
            _mappedViewAccessor?.Dispose();
            _mappedFile?.Dispose();
        }
        #endregion
    }

}
