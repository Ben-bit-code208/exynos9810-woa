// SPDX-License-Identifier: LicenseRef-PublicDomain
// Vendored from the public-domain 7-Zip LZMA SDK by Igor Pavlov
// (https://www.7-zip.org/sdk.html). Placed in the public domain by its author.
// Namespaced under S9Woa and compiled with warnings suppressed; the source is
// used verbatim so the recovery ramdisk it produces matches the LZMA the kernel
// expects. See installer/src/S9Woa.Installer.Core/Twrp/Lzma/README.md.
#nullable disable
#pragma warning disable
//Public Domain
// IMatchFinder.cs

using System;

namespace S9Woa.Installer.Core.Twrp.Lzma.SevenZip.Compression.LZ
{
	interface IInWindowStream
	{
		void SetStream(System.IO.Stream inStream);
		void Init();
		void ReleaseStream();
		Byte GetIndexByte(Int32 index);
		UInt32 GetMatchLen(Int32 index, UInt32 distance, UInt32 limit);
		UInt32 GetNumAvailableBytes();
	}

	interface IMatchFinder : IInWindowStream
	{
		void Create(UInt32 historySize, UInt32 keepAddBufferBefore,
				UInt32 matchMaxLen, UInt32 keepAddBufferAfter);
		UInt32 GetMatches(UInt32[] distances);
		void Skip(UInt32 num);
	}
}
