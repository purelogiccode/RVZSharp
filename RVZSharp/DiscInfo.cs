using System.Text;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.Wii;

namespace RVZSharp;

/// <summary>
/// Disc volume metadata read from the decoded disc header (Dolphin: <c>VolumeDisc</c>):
/// game ID, maker ID, revision, internal name, region, country and title ID. Works on any
/// <see cref="IBlobReader"/>, so it answers the same questions for a plain ISO and for every
/// supported container.
/// </summary>
public sealed class DiscInfo
{
    private static readonly Encoding Cp1252 = GetCodePageEncoding(1252);
    private static readonly Encoding ShiftJis = GetCodePageEncoding(932);

    private DiscInfo(DiscType discType, string gameId, string makerId, byte revision,
        string internalName, string region, string country, char countryCode, ulong? titleId)
    {
        DiscType = discType;
        GameId = gameId;
        MakerId = makerId;
        Revision = revision;
        InternalName = internalName;
        Region = region;
        Country = country;
        CountryCode = countryCode;
        TitleId = titleId;
    }

    /// <summary>The disc type (GameCube or Wii) the header magic declares.</summary>
    public DiscType DiscType { get; }

    /// <summary>The 6-character game ID (bytes 0-5, non-alphanumeric bytes replaced by '-').</summary>
    public string GameId { get; }

    /// <summary>The 2-character maker ID (bytes 4-5, non-alphanumeric bytes replaced by '-').</summary>
    public string MakerId { get; }

    /// <summary>The disc revision byte (header offset 7).</summary>
    public byte Revision { get; }

    /// <summary>The internal disc name (0x60 bytes at offset 0x20, up to the first NUL;
    /// CP1252, or Shift-JIS for NTSC-J discs — Dolphin: Volume::DecodeString).</summary>
    public string InternalName { get; }

    /// <summary>
    /// The region: <c>NTSC-J</c>, <c>NTSC-U</c>, <c>PAL</c>, <c>NTSC-K</c> or <c>Unknown</c>
    /// (Dolphin: RegionCodeToRegion, using the GC region word at 0x458 or the Wii one at 0x4E000).
    /// </summary>
    public string Region { get; }

    /// <summary>
    /// The country derived from the game ID's country byte: <c>Japan</c>, <c>USA</c>,
    /// <c>Europe</c>, <c>Korea</c>, <c>Taiwan</c>, <c>World</c>, <c>Germany</c>, <c>France</c>,
    /// <c>Italy</c>, <c>Netherlands</c>, <c>Russia</c>, <c>Spain</c>, <c>Australia</c> or
    /// <c>Unknown</c> (Dolphin: VolumeDisc::GetCountry).
    /// </summary>
    public string Country { get; }

    /// <summary>The raw country byte from the game ID (header offset 3).</summary>
    public char CountryCode { get; }

    /// <summary>
    /// The 64-bit title ID from the game partition's ticket (Wii only; Dolphin:
    /// VolumeWii::GetTitleID). Null for GameCube discs or when no game partition is present.
    /// </summary>
    public ulong? TitleId { get; }

    /// <summary>
    /// Reads the disc metadata, or returns null when the image does not carry a GameCube/Wii
    /// disc header magic or is too short to contain one.
    /// </summary>
    /// <param name="reader">The blob to inspect.</param>
    /// <returns>The metadata, or null when <paramref name="reader"/> is not a disc image.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    public static DiscInfo? TryRead(IBlobReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.Length < 0x80)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[0x80];
        if (reader.ReadAt(0, header) != header.Length)
        {
            return null;
        }

        var isWii = Be32(header, 0x18) == WiiVolume.WII_MAGIC;
        var isGc = Be32(header, 0x1C) == WiiVolume.GC_MAGIC;
        if (!isWii && !isGc)
        {
            return null;
        }

        var discType = isWii ? DiscType.Wii : DiscType.GameCube;
        var gameId = FilterGameId(header[..6]);
        var makerId = FilterGameId(header.Slice(4, 2));
        var revision = header[7];
        var countryCode = (char)header[3];

        var region = ReadRegion(reader, isWii);
        var internalName = DecodeInternalName(reader, region);
        var titleId = isWii ? ReadTitleId(reader) : null;
        var country = CountryCodeToCountry(countryCode, isWii, region, revision);
        // Dolphin falls back to the region's typical country when the country byte
        // contradicts the region (VolumeDisc.cpp:93-99).
        if (CountryCodeToRegion(countryCode, isWii, region, revision) != region)
        {
            country = TypicalCountryForRegion(region);
        }

        return new DiscInfo(discType, gameId, makerId, revision, internalName, region, country,
            countryCode, titleId);
    }

    /// <summary>
    /// Reads the disc metadata and throws when the image is not a GameCube/Wii disc
    /// (Dolphin: TryCreateDisc).
    /// </summary>
    /// <param name="reader">The blob to inspect.</param>
    /// <returns>The disc metadata.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    /// <exception cref="RvzFormatException">The image carries no disc header magic.</exception>
    public static DiscInfo Read(IBlobReader reader)
    {
        return TryRead(reader)
               ?? throw new RvzFormatException(
                   "The image does not carry a GameCube/Wii disc header magic.");
    }

    /// <summary>
    /// 6 bytes at offset 0; any non-alphanumeric byte becomes '-', including NUL and
    /// the country byte (Dolphin: Volume.cpp:46-56).
    /// </summary>
    private static string FilterGameId(ReadOnlySpan<byte> id)
    {
        var chars = new char[id.Length];
        for (var i = 0; i < id.Length; i++)
        {
            var c = (char)id[i];
            chars[i] = c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z'
                ? c
                : '-';
        }

        return new string(chars);
    }

    /// <summary>0x60 bytes at 0x20, up to the first NUL; CP1252, or Shift-JIS for NTSC-J
    /// (Dolphin: Volume.cpp:39-44).</summary>
    private static string DecodeInternalName(IBlobReader disc, string region)
    {
        var raw = new byte[0x60];
        if (disc.ReadAt(0x20, raw) != raw.Length)
        {
            return string.Empty;
        }

        var end = Array.IndexOf(raw, (byte)0);
        if (end < 0)
        {
            end = raw.Length;
        }

        return NameEncoding(region).GetString(raw, 0, end);
    }

    private static Encoding NameEncoding(string region)
    {
        return region == "NTSC-J" ? ShiftJis : Cp1252;
    }

    private static Encoding GetCodePageEncoding(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage);
    }

    /// <summary>
    /// Title ID from the game partition's ticket (u64 BE at ticket + 0x1DC).
    /// The game partition is the one with type 0 in the partition table.
    /// </summary>
    private static ulong? ReadTitleId(IBlobReader disc)
    {
        try
        {
            Span<byte> value = stackalloc byte[8];
            foreach (var partition in WiiVolume.GetPartitions(disc))
            {
                if (partition.Type != 0)
                {
                    continue;
                }

                if (disc.ReadAt((long)partition.Offset + 0x1DC, value) == value.Length)
                {
                    return Be64(value, 0);
                }
            }
        }
        catch (RvzException)
        {
            // A damaged partition table means no title ID, not a failed metadata read.
        }

        return null;
    }

    private static string ReadRegion(IBlobReader disc, bool isWii)
    {
        // GC: region word at 0x458; Wii: region word at 0x4E000.
        var offset = isWii ? 0x4E000 : 0x458;
        if (disc.Length < offset + 4)
        {
            return "Unknown";
        }

        Span<byte> value = stackalloc byte[4];
        if (disc.ReadAt(offset, value) != value.Length)
        {
            return "Unknown";
        }

        var code = Be32(value, 0);
        return code switch
        {
            0 => "NTSC-J",
            1 => "NTSC-U",
            2 => "PAL",
            4 => "NTSC-K",
            _ => "Unknown"
        };
    }

    /// <summary>Dolphin's CountryCodeToRegion (Enums.cpp:213-268).</summary>
    private static string CountryCodeToRegion(char code, bool isWii, string region, byte revision)
    {
        var isGc = !isWii;
        switch (code)
        {
            case '\x02':
                return region; // Wii Menu (same title ID for all regions)
            case 'J':
                return "NTSC-J";
            case 'W':
                // Only the Nordic version of Ratatouille (Wii) is PAL; otherwise Korean
                // GC games in English or Taiwanese Wii games.
                return region == "PAL" ? "PAL" : "NTSC-J";
            case 'E':
                if (!isGc)
                {
                    return "NTSC-U"; // the most common country code for NTSC-U
                }

                return revision >= 0x30 ? "NTSC-J" : "NTSC-U"; // Korean GC games in English
            case 'B':
            case 'N':
                return "NTSC-U";
            case 'X':
            case 'Y':
            case 'Z':
                // Additional language versions, store-exclusive versions, special versions.
                return region == "NTSC-U" ? "NTSC-U" : "PAL";
            case 'D':
            case 'F':
            case 'H':
            case 'I':
            case 'L':
            case 'M':
            case 'P':
            case 'R':
            case 'S':
            case 'U':
            case 'V':
                return "PAL";
            case 'K':
            case 'Q':
            case 'T':
                // All Korean, but the NTSC-K region does not exist on GC.
                return isGc ? "NTSC-J" : "NTSC-K";
            default:
                return "Unknown";
        }
    }

    /// <summary>Dolphin's TypicalCountryForRegion (Enums.cpp:173-187).</summary>
    private static string TypicalCountryForRegion(string region)
    {
        return region switch
        {
            "NTSC-J" => "Japan",
            "NTSC-U" => "USA",
            "PAL" => "Europe",
            "NTSC-K" => "Korea",
            _ => "Unknown"
        };
    }

    /// <summary>Dolphin's CountryCodeToCountry (Enums.cpp).</summary>
    private static string CountryCodeToCountry(char code, bool isWii, string region, byte revision)
    {
        var isGc = !isWii;
        switch (code)
        {
            case 'A':
                return "World";
            case 'X':
            case 'Y':
            case 'Z':
                return region == "NTSC-U" ? "USA" : "Europe";
            case 'W':
                if (isGc)
                {
                    return "Korea";
                }

                return region == "PAL" ? "Europe" : "Taiwan";
            case 'D':
                return "Germany";
            case 'L':
            case 'M':
            case 'V':
            case 'P':
                return "Europe";
            case 'U':
                return "Australia";
            case 'F':
                return "France";
            case 'I':
                return "Italy";
            case 'H':
                return "Netherlands";
            case 'R':
                return "Russia";
            case 'S':
                return "Spain";
            case 'E':
                if (!isGc)
                {
                    return "USA";
                }

                if (revision >= 0x30)
                {
                    return "Korea";
                }

                return region == "NTSC-J" ? "Korea" : "USA";
            case 'B':
            case 'N':
                return "USA";
            case 'J':
                return "Japan";
            case 'K':
            case 'Q':
            case 'T':
                return "Korea";
            default:
                return "Unknown";
        }
    }

    private static uint Be32(ReadOnlySpan<byte> data, int offset)
    {
        return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8)
                      | data[offset + 3]);
    }

    private static ulong Be64(ReadOnlySpan<byte> data, int offset)
    {
        return ((ulong)Be32(data, offset) << 32) | Be32(data, offset + 4);
    }
}
