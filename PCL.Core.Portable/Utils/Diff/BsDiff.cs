// Modifications Copyright (c) 2026 PCL N contributors.
/*
部分内容参考了 https://github.com/LogosBible/bsdiff.net 的实现

Copyright 2010-2024 Logos Bible Software

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.


Copyright 2003-2005 Colin Percival
All rights reserved

Redistribution and use in source and binary forms, with or without
modification, are permitted providing that the following conditions
are met:
1. Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in the
    documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE AUTHOR ``AS IS'' AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED.  IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS
OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING
IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
*/

using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using ICSharpCode.SharpZipLib.BZip2;
using PCL.Core.Logging;

namespace PCL.Core.Utils.Diff;


public class BsDiff : IBinaryDiff
{
	private const int HeaderSize = 32; // 32-byte header
	private const int HeaderVersionIndex = 0;
	private const long HeaderVersion = 0x3034464649445342; // "BSDIFF40" in little-endian
	private const int HeaderCtrlIndex = 8;
	private const int HeaderDiffIndex = 16;
	private const int HeaderNewSizeIndex = 24;
	private const int IoBufferSize = 128 * 1024;

	private static readonly string ByteAdditionPath =
		Avx2.IsSupported ? "AVX2-256" :
		AdvSimd.IsSupported ? "AdvSimd-128" :
		Sse2.IsSupported ? "SSE2-128" :
		"Scalar";

	/*
File format:
	0	8	"BSDIFF40"
	8	8	X
	16	8	Y
	24	8	sizeof(newfile)
	32	X	bzip2(control block)
	32+X	Y	bzip2(diff block)
	32+X+Y	???	bzip2(extra block)
with control block a set of triples (x,y,z) meaning "add x bytes
from oldfile to x bytes from the diff block; copy y bytes from the
extra block; seek forwards in oldfile by z bytes".
*/
	
	public async Task<byte[]> ApplyAsync(byte[] originData, byte[] diffData)
	{
		return await Task.Run(() =>
		{
			if (diffData.Length < HeaderSize)
					throw new InvalidDataException("Diff file size is less than the header size");
			if (BitConverter.ToInt64(diffData, HeaderVersionIndex) != HeaderVersion)
					throw new InvalidDataException("Diff file version is wrong");
			// 读取 Header 信息
			var ctrlLen = BitConverter.ToInt64(diffData, HeaderCtrlIndex);
			var diffLen = BitConverter.ToInt64(diffData, HeaderDiffIndex);
			var newLen = BitConverter.ToInt64(diffData, HeaderNewSizeIndex);
			var extraLen = diffData.Length - HeaderSize - ctrlLen - diffLen;

			if (ctrlLen < 0 || diffLen < 0 || extraLen < 0)
					throw new InvalidDataException("Block size is negative");
			if (newLen < 0)
					throw new InvalidDataException("Final file size info is negative");
			if (HeaderSize + ctrlLen + diffLen + extraLen > diffData.Length)
					throw new InvalidDataException("Diff file size info is not correct");

			var ctrlContent = new byte[ctrlLen];
			// 获取 Control 数据
			long curOffset = HeaderSize;
			Array.Copy(diffData, curOffset, ctrlContent, 0, ctrlLen);
			using var ctrlStream = new BZip2InputStream(new MemoryStream(ctrlContent));
			using var ctrlReader = new BinaryReader(ctrlStream);
			// 获取 Diff 数据
			curOffset += ctrlLen;
			var diffContent = new byte[diffLen];
			Array.Copy(diffData, curOffset, diffContent, 0, diffLen);
			using var diffStream = new BZip2InputStream(new MemoryStream(diffContent));
			using var diffReader = new BinaryReader(diffStream);
			// 获取 Extra 数据
			curOffset += diffLen;
			var extraContent = new byte[extraLen];
			Array.Copy(diffData, curOffset, extraContent, 0, extraLen);
			using var extraStream = new BZip2InputStream(new MemoryStream(extraContent));
			using var extraReader = new BinaryReader(extraStream);
			
			var ret = new byte[newLen];

			byte[] ioBuffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
			try
			{
				PortableLog.Debug("BsDiff", $"开始应用补丁；字节合并路径={ByteAdditionPath}。");
				long newDataPos = 0;
				long oldDataPos = 0;
				while (newDataPos < newLen)
				{
					var addRange = ReadInt64(ctrlReader.ReadBytes(8));
					var copyRange = ReadInt64(ctrlReader.ReadBytes(8));
					var seekPos = ReadInt64(ctrlReader.ReadBytes(8));

					if (addRange < 0 || copyRange < 0)
						throw new InvalidDataException("Control range is negative");

					// 将差异块批量读入复用缓冲区；重叠旧数据用运行时 ISA 分派做模 256 相加。
					if (newDataPos + addRange > newLen)
						throw new InvalidDataException(
							$"Add range overflows, want add {addRange}, but only have {newLen - newDataPos} left");

					ApplyAddRange(
						diffReader.BaseStream,
						originData,
						ret,
						oldDataPos,
						newDataPos,
						addRange,
						ioBuffer);

					newDataPos += addRange;
					oldDataPos += addRange;

					// Extra 块无需逐字节读取，直接填充目标区间。
					if (newDataPos + copyRange > newLen)
						throw new InvalidDataException(
							$"Copy range overflows, want copy {copyRange}, but only have {newLen - newDataPos} left");

					extraReader.BaseStream.ReadExactly(ret.AsSpan(
						checked((int)newDataPos),
						checked((int)copyRange)));
					newDataPos += copyRange;

					// 原有的切换到指定位置继续读取。
					oldDataPos += seekPos;
					if (oldDataPos > originData.Length)
						throw new InvalidDataException(
						$"Old data pos overflows, current old data length = {originData.Length}, but want {oldDataPos}");
				}

				return ret;
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(ioBuffer);
			}
		});
	}

	private static void ApplyAddRange(
		Stream diffStream,
		byte[] originData,
		byte[] targetData,
		long oldDataPos,
		long newDataPos,
		long count,
		byte[] ioBuffer)
	{
		long processed = 0;
		while (processed < count)
		{
			int chunkLength = (int)Math.Min(ioBuffer.Length, count - processed);
			Span<byte> diffChunk = ioBuffer.AsSpan(0, chunkLength);
			diffStream.ReadExactly(diffChunk);

			Span<byte> targetChunk = targetData.AsSpan(
				checked((int)(newDataPos + processed)),
				chunkLength);
			diffChunk.CopyTo(targetChunk);

			long sourceStart = checked(oldDataPos + processed);
			long sourceEnd = checked(sourceStart + chunkLength);
			long overlapStart = Math.Max(sourceStart, 0);
			long overlapEnd = Math.Min(sourceEnd, originData.LongLength);
			if (overlapStart < overlapEnd)
			{
				int chunkOffset = checked((int)(overlapStart - sourceStart));
				int originOffset = checked((int)overlapStart);
				int overlapLength = checked((int)(overlapEnd - overlapStart));
				AddModulo256(
					diffChunk.Slice(chunkOffset, overlapLength),
					originData.AsSpan(originOffset, overlapLength),
					targetChunk.Slice(chunkOffset, overlapLength));
			}

			processed += chunkLength;
		}
	}

	private static void AddModulo256(
		ReadOnlySpan<byte> left,
		ReadOnlySpan<byte> right,
		Span<byte> destination)
	{
		int offset = 0;
		if (Avx2.IsSupported)
		{
			ReadOnlySpan<Vector256<byte>> leftVectors = MemoryMarshal.Cast<byte, Vector256<byte>>(left);
			ReadOnlySpan<Vector256<byte>> rightVectors = MemoryMarshal.Cast<byte, Vector256<byte>>(right);
			Span<Vector256<byte>> destinationVectors = MemoryMarshal.Cast<byte, Vector256<byte>>(destination);
			for (int index = 0; index < leftVectors.Length; index++)
				destinationVectors[index] = Avx2.Add(leftVectors[index], rightVectors[index]);
			offset = leftVectors.Length * Vector256<byte>.Count;
		}
		else if (AdvSimd.IsSupported)
		{
			ReadOnlySpan<Vector128<byte>> leftVectors = MemoryMarshal.Cast<byte, Vector128<byte>>(left);
			ReadOnlySpan<Vector128<byte>> rightVectors = MemoryMarshal.Cast<byte, Vector128<byte>>(right);
			Span<Vector128<byte>> destinationVectors = MemoryMarshal.Cast<byte, Vector128<byte>>(destination);
			for (int index = 0; index < leftVectors.Length; index++)
				destinationVectors[index] = AdvSimd.Add(leftVectors[index], rightVectors[index]);
			offset = leftVectors.Length * Vector128<byte>.Count;
		}
		else if (Sse2.IsSupported)
		{
			ReadOnlySpan<Vector128<byte>> leftVectors = MemoryMarshal.Cast<byte, Vector128<byte>>(left);
			ReadOnlySpan<Vector128<byte>> rightVectors = MemoryMarshal.Cast<byte, Vector128<byte>>(right);
			Span<Vector128<byte>> destinationVectors = MemoryMarshal.Cast<byte, Vector128<byte>>(destination);
			for (int index = 0; index < leftVectors.Length; index++)
				destinationVectors[index] = Sse2.Add(leftVectors[index], rightVectors[index]);
			offset = leftVectors.Length * Vector128<byte>.Count;
		}

		for (; offset < left.Length; offset++)
			destination[offset] = unchecked((byte)(left[offset] + right[offset]));
	}

	public Task<byte[]> MakeAsync(byte[] originData, byte[] newData)
	{
		ArgumentNullException.ThrowIfNull(originData);
		ArgumentNullException.ThrowIfNull(newData);

		return Task.Run(() =>
		{
			// 生成一个合法但不做最小化的 BSDIFF40 补丁：diff block 为空，
			// extra block 直接存放目标文件。后续可在不改接口的前提下替换为真正的最小差分算法。
			byte[] ctrlBlock = Compress(BuildControlBlock(addRange: 0, copyRange: newData.Length, seek: 0));
			byte[] diffBlock = Compress([]);
			byte[] extraBlock = Compress(newData);

			using MemoryStream patch = new(HeaderSize + ctrlBlock.Length + diffBlock.Length + extraBlock.Length);
			WriteInt64(patch, HeaderVersion);
			WriteInt64(patch, ctrlBlock.Length);
			WriteInt64(patch, diffBlock.Length);
			WriteInt64(patch, newData.Length);
			patch.Write(ctrlBlock);
			patch.Write(diffBlock);
			patch.Write(extraBlock);
			return patch.ToArray();
		});
	}

	private static byte[] BuildControlBlock(long addRange, long copyRange, long seek)
	{
		using MemoryStream stream = new(24);
		WriteControlInt64(stream, addRange);
		WriteControlInt64(stream, copyRange);
		WriteControlInt64(stream, seek);
		return stream.ToArray();
	}

	private static byte[] Compress(byte[] data)
	{
		using MemoryStream stream = new();
		using (BZip2OutputStream output = new(stream))
			output.Write(data, 0, data.Length);
		return stream.ToArray();
	}

	private static void WriteInt64(Stream stream, long value)
	{
		Span<byte> buffer = stackalloc byte[8];
		BitConverter.TryWriteBytes(buffer, value);
		stream.Write(buffer);
	}

	private static void WriteControlInt64(Stream stream, long value)
	{
		Span<byte> buffer = stackalloc byte[8];
		long encoded = value < 0 ? -value : value;
		BitConverter.TryWriteBytes(buffer, encoded);
		if (value < 0)
			buffer[7] |= 0x80;
		stream.Write(buffer);
	}

	internal static long ReadInt64(byte[] buffer, int offset = 0)
	{
		// 手动组合小端序的 long 值
		var value = ((long)buffer[offset] << 0)  | ((long)buffer[offset + 1] << 8) |
		            ((long)buffer[offset + 2] << 16) | ((long)buffer[offset + 3] << 24) |
		            ((long)buffer[offset + 4] << 32) | ((long)buffer[offset + 5] << 40) |
		            ((long)buffer[offset + 6] << 48) | ((long)buffer[offset + 7] << 56);

		// 原始位运算逻辑保持不变
		var mask = value >> 63;
		return (~mask & value) |
		       (((value & unchecked((long)0x8000000000000000)) - value) & mask);
	}
}
