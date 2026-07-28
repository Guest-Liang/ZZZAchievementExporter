using System.Buffers.Binary;
using System.Text;

namespace ZZZae.Hook;

/// <summary>
/// 当前玩家 UID 的运行时根槽。RVA 只用于诊断；读取时始终使用已经解析出的地址。
/// </summary>
internal readonly record struct CurrentUidLocation(
    nint RootSlotAddress,
    uint RootSlotRva,
    uint ServiceTypeSlotRva,
    int EquivalentPathCount
);

/// <summary>
/// 从 GameAssembly.dll 的代码结构中定位当前玩家 UID 所依赖的运行时根槽。
///
/// 这里不使用固定 RVA、完整函数入口或 metadata usage 编号。定位锚定的是 getter
/// 正常路径中稳定的对象布局：
///
/// class -> +0x40 -> +0x10 -> class -> +0x50 -> static owner
/// owner -> +0x40 -> UID service -> +0x40 uint32 UID
///
/// 同一 getter 可能存在原生与 HybridCLR 等价代码路径，所以允许多个代码命中；
/// 但它们必须全部归一到同一个根全局槽和服务类型槽，否则失败关闭。
/// </summary>
internal static unsafe class CurrentUidLocator
{
    public const int LocatorVersion = 1;

    private const int CandidateLength = 0x8E;
    private const int MarkerOffset = 0x07;
    private const int ServiceLoadOffset = 0x68;
    private const int RipRelativeInstructionLength = 7;
    private const int MaximumCandidates = 64;

    private static ReadOnlySpan<byte> Marker => [0x48, 0x8B, 0x30, 0xF6, 0x86, 0xCC, 0x00, 0x00, 0x00, 0x01];

    private readonly record struct Candidate(uint CodeRva, uint RootSlotRva, uint ServiceTypeSlotRva);

    public static CurrentUidLocation Locate(nint moduleBase)
    {
        var image = (byte*)moduleBase;
        if (*(ushort*)image != 0x5A4D)
        {
            throw new InvalidDataException("GameAssembly.dll 不含有效的 DOS 头，无法定位当前 UID");
        }

        var ntOffset = *(int*)(image + 0x3C);
        if (ntOffset <= 0 || *(uint*)(image + ntOffset) != 0x0000_4550)
        {
            throw new InvalidDataException("GameAssembly.dll 不含有效的 PE 头，无法定位当前 UID");
        }

        var fileHeader = image + ntOffset + sizeof(uint);
        var sectionCount = *(ushort*)(fileHeader + 2);
        var optionalHeaderSize = *(ushort*)(fileHeader + 16);
        var optionalHeader = fileHeader + 20;
        if (*(ushort*)optionalHeader != 0x020B)
        {
            throw new InvalidDataException("GameAssembly.dll 不是 PE32+ 映像，无法定位当前 UID");
        }

        var imageSize = *(uint*)(optionalHeader + 56);
        var sectionTable = optionalHeader + optionalHeaderSize;
        var candidates = new List<Candidate>();

        ScanExecutableSections(image, sectionTable, sectionCount, imageSize, candidates);

        if (candidates.Count == 0)
        {
            throw new InvalidDataException("找不到符合当前玩家 UID getter 对象布局的代码路径；游戏结构可能已经改变");
        }

        var selected = candidates[0];
        foreach (var candidate in candidates)
        {
            if (
                candidate.RootSlotRva != selected.RootSlotRva
                || candidate.ServiceTypeSlotRva != selected.ServiceTypeSlotRva
            )
            {
                throw new InvalidDataException(DescribeAmbiguousCandidates(candidates));
            }
        }

        return new CurrentUidLocation(
            (nint)(image + selected.RootSlotRva),
            selected.RootSlotRva,
            selected.ServiceTypeSlotRva,
            candidates.Count
        );
    }

    private static void ScanExecutableSections(
        byte* image,
        byte* sectionTable,
        int sectionCount,
        uint imageSize,
        List<Candidate> candidates
    )
    {
        for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            var section = sectionTable + (sectionIndex * 40);
            var virtualSize = *(uint*)(section + 8);
            var virtualAddress = *(uint*)(section + 12);
            var characteristics = *(uint*)(section + 36);
            if (
                (characteristics & NativeMethods.ImageScnMemExecute) == 0
                || virtualSize < CandidateLength
                || virtualAddress >= imageSize
            )
            {
                continue;
            }

            var safeSize = Math.Min(virtualSize, imageSize - virtualAddress);
            var span = new ReadOnlySpan<byte>(image + virtualAddress, checked((int)safeSize));
            ScanSection(span, virtualAddress, imageSize, image, sectionTable, sectionCount, candidates);
        }
    }

    private static void ScanSection(
        ReadOnlySpan<byte> section,
        uint sectionRva,
        uint imageSize,
        byte* image,
        byte* sectionTable,
        int sectionCount,
        List<Candidate> candidates
    )
    {
        var searchOffset = 0;
        while (searchOffset <= section.Length - Marker.Length)
        {
            var relative = section[searchOffset..].IndexOf(Marker);
            if (relative < 0)
            {
                return;
            }

            var markerOffset = searchOffset + relative;
            var candidateOffset = markerOffset - MarkerOffset;
            if (
                candidateOffset >= 0
                && candidateOffset <= section.Length - CandidateLength
                && IsUidGetterPath(section, candidateOffset)
            )
            {
                var codeRva = sectionRva + (uint)candidateOffset;
                if (
                    TryDecodeRipTarget(
                        section,
                        candidateOffset,
                        codeRva,
                        instructionOffset: 0,
                        imageSize,
                        out var rootSlotRva
                    )
                    && TryDecodeRipTarget(
                        section,
                        candidateOffset,
                        codeRva,
                        ServiceLoadOffset,
                        imageSize,
                        out var serviceTypeSlotRva
                    )
                    && IsWritableDataSlot(image, sectionTable, sectionCount, imageSize, rootSlotRva)
                    && IsWritableDataSlot(image, sectionTable, sectionCount, imageSize, serviceTypeSlotRva)
                )
                {
                    candidates.Add(new Candidate(codeRva, rootSlotRva, serviceTypeSlotRva));
                    if (candidates.Count > MaximumCandidates)
                    {
                        throw new InvalidDataException("当前玩家 UID getter 的结构命中数量异常，拒绝继续定位");
                    }
                }
            }

            searchOffset = markerOffset + 1;
        }
    }

    private static bool IsUidGetterPath(ReadOnlySpan<byte> code, int start)
    {
        // RIP 相对地址、call/jump 位移会随重编译变化，故只核对操作码、寄存器流、
        // IL2CPP 初始化标志偏移以及真正有语义的对象字段偏移
        return Matches(code, start + 0x00, [0x48, 0x8B, 0x05])
            && Matches(code, start + 0x07, [0x48, 0x8B, 0x30, 0xF6, 0x86, 0xCC, 0x00, 0x00, 0x00, 0x01, 0x0F, 0x84])
            && Matches(
                code,
                start + 0x17,
                [0x48, 0x8B, 0x46, 0x40, 0x48, 0x8B, 0x78, 0x10, 0x48, 0x85, 0xFF, 0x0F, 0x84]
            )
            && Matches(code, start + 0x28, [0xF6, 0x87, 0xCC, 0x00, 0x00, 0x00, 0x01, 0x0F, 0x84])
            && Matches(code, start + 0x35, [0x48, 0x8B, 0x47, 0x50, 0x48, 0x8B, 0x30, 0x48, 0x85, 0xF6, 0x0F, 0x84])
            && Matches(code, start + 0x45, [0x80, 0x3D])
            && code[start + 0x4B] == 0
            && Matches(code, start + 0x4C, [0x0F, 0x84])
            && Matches(code, start + 0x52, [0x80, 0x3D])
            && code[start + 0x58] == 0
            && Matches(code, start + 0x59, [0x0F, 0x85])
            && Matches(code, start + 0x5F, [0x48, 0x8B, 0x46, 0x40, 0x48, 0x85, 0xC0, 0x75])
            && Matches(code, start + 0x68, [0x48, 0x8B, 0x15])
            && Matches(code, start + 0x6F, [0x48, 0x89, 0xF1, 0xE8])
            && Matches(code, start + 0x77, [0x48, 0x89, 0x46, 0x40, 0x48, 0x85, 0xC0, 0x0F, 0x84])
            && Matches(code, start + 0x84, [0x8B, 0x40, 0x40, 0x48, 0x83, 0xC4, 0x28, 0x5F, 0x5E, 0xC3]);
    }

    private static bool Matches(ReadOnlySpan<byte> code, int offset, ReadOnlySpan<byte> expected)
    {
        return offset >= 0
            && offset <= code.Length - expected.Length
            && code.Slice(offset, expected.Length).SequenceEqual(expected);
    }

    private static bool TryDecodeRipTarget(
        ReadOnlySpan<byte> section,
        int candidateOffset,
        uint candidateRva,
        int instructionOffset,
        uint imageSize,
        out uint targetRva
    )
    {
        var displacementOffset = candidateOffset + instructionOffset + 3;
        var displacement = BinaryPrimitives.ReadInt32LittleEndian(section.Slice(displacementOffset, sizeof(int)));
        var target = (long)candidateRva + instructionOffset + RipRelativeInstructionLength + displacement;
        if (target < 0 || target > imageSize - sizeof(nint))
        {
            targetRva = 0;
            return false;
        }

        targetRva = (uint)target;
        return true;
    }

    private static bool IsWritableDataSlot(byte* image, byte* sectionTable, int sectionCount, uint imageSize, uint rva)
    {
        _ = image;

        for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            var section = sectionTable + (sectionIndex * 40);
            var virtualSize = *(uint*)(section + 8);
            var virtualAddress = *(uint*)(section + 12);
            var characteristics = *(uint*)(section + 36);
            if (
                (characteristics & NativeMethods.ImageScnMemRead) == 0
                || (characteristics & NativeMethods.ImageScnMemWrite) == 0
                || (characteristics & NativeMethods.ImageScnMemExecute) != 0
                || virtualAddress >= imageSize
            )
            {
                continue;
            }

            var safeSize = Math.Min(virtualSize, imageSize - virtualAddress);
            var sectionEnd = (ulong)virtualAddress + safeSize;
            if (rva >= virtualAddress && (ulong)rva + (uint)sizeof(nint) <= sectionEnd)
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeAmbiguousCandidates(List<Candidate> candidates)
    {
        var description = new StringBuilder();
        foreach (var candidate in candidates)
        {
            if (description.Length != 0)
            {
                description.Append('、');
            }

            description.Append(
                $"代码 RVA 0x{candidate.CodeRva:X} → "
                    + $"RootSlot 0x{candidate.RootSlotRva:X} / "
                    + $"ServiceTypeSlot 0x{candidate.ServiceTypeSlotRva:X}"
            );
        }

        return $"找到多个不同的当前玩家 UID getter 目标，无法安全选择：{description}";
    }
}
