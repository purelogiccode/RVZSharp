using System.Security.Cryptography;
using RVZSharp.Blobs;
using RVZSharp.Models;

namespace RVZSharp.Slow.Tests;

/// <summary>
/// Byte-exact validation of the decoder and writer against REAL RVZ files on disk.
///
/// The expected ISO SHA-1 hashes come from the official No-Intro DAT files
/// (<c>References/rvz-1.0.3/testdata/Nintendo - GameCube - Datfile…dat</c> and
/// <c>Nintendo - Wii - Datfile…dat</c>). A test that decodes a real file to its No-Intro
/// SHA-1 proves the reader reproduces the original disc image byte-for-byte.
///
/// The files live on a local drive (<c>F:\Nintendo GameCube</c> / <c>F:\Nintendo Wii</c> by
/// default; override with <c>RVZ_REAL_GC_DIR</c> / <c>RVZ_REAL_WII_DIR</c>) and are NOT part
/// of the repository, so every test early-returns (no-op) when its file is absent. Run the
/// suite on a machine that has the games mounted to execute them.
/// </summary>
public static class RealRvzCatalog
{
    /// <summary>(display name, relative file name, expected ISO SHA-1, expected ISO size).</summary>
    public record RealRvz(string Name, string File, string Sha1, long IsoSize);

    /// <summary>GameCube catalog directory (override with <c>RVZ_REAL_GC_DIR</c>).</summary>
    public static readonly string GcDir =
        Environment.GetEnvironmentVariable("RVZ_REAL_GC_DIR") ?? @"F:\Nintendo GameCube";

    /// <summary>Wii catalog directory (override with <c>RVZ_REAL_WII_DIR</c>).</summary>
    public static readonly string WiiDir =
        Environment.GetEnvironmentVariable("RVZ_REAL_WII_DIR") ?? @"F:\Nintendo Wii";

    /// <summary>The GameCube catalog (45 games with their No-Intro SHA-1 and ISO size).</summary>
    public static readonly RealRvz[] GameCube =
    [
        new("Advance Game Port (Unl)", "Advance Game Port (USA) (Unl).rvz",
            "305fe256e4927b1e8fb54a02e886197b97263508", 1459978240),
        new("Advance Game Port (Unl) (Rev 1)", "Advance Game Port (USA) (Unl) (Rev 1).rvz",
            "4e448ab4189f3ab09f4b5a9dbf2355792d8c956e", 1459978240),
        new("Call of Duty - Finest Hour", "Call of Duty - Finest Hour (USA).rvz",
            "4ce36ab8246ee4d11f636cd89b6906b07ceb5519", 1459978240),
        new("Crash Bandicoot - The Wrath of Cortex", "Crash Bandicoot - The Wrath of Cortex (USA).rvz",
            "08baf3fdef38908ee2d0a826afc728198048bd38", 1459978240),
        new("Evolution Snowboarding", "Evolution Snowboarding (USA).rvz",
            "3e6dc183cb2bb248e443f15b14eacb1d174016a0", 1459978240),
        new("Game Boy Player Start-Up Disc (Rev 2)", "Game Boy Player Start-Up Disc (USA) (Rev 2).rvz",
            "f2439bbe1ff64133050fbc00574be8478210a958", 1459978240),
        new("Harvest Moon - A Wonderful Life", "Harvest Moon - A Wonderful Life (USA).rvz",
            "8d0f26063d0ebf2ea3e7a18e86de01b8cc1e5191", 1459978240),
        new("Kelly Slater's Pro Surfer", "Kelly Slater's Pro Surfer (USA).rvz",
            "fe8b890354a796ceae9efd8316706ccc65e41861", 1459978240),
        new("Midway Arcade Treasures", "Midway Arcade Treasures (USA).rvz",
            "cd0c7f3fc49bbe42bda3eb6494a027c90adfb82c", 1459978240),
        new("Monster Jam - Maximum Destruction", "Monster Jam - Maximum Destruction (USA).rvz",
            "57fcc43b8c74c6e631c701e59e2a5f27d120cf60", 1459978240),
        new("Open Season (En,Fr,Es)", "Open Season (USA) (En,Fr,Es).rvz",
            "61e04f1dbfea8c34024638bbfda544b50abe33fe", 1459978240),
        new("RedCard 20-03", "RedCard 20-03 (USA).rvz",
            "44b78ec19415f9f9a5a7f294f482d8011e8716b0", 1459978240),
        new("Robots", "Robots (USA).rvz",
            "98cef132f3ae139414d089df2e09b9c63d7833e7", 1459978240),
        new("Sum of All Fears, The", "Sum of All Fears, The (USA).rvz",
            "c7214e84362f41983703328a187f4e056177da37", 1459978240),
        new("Tak and the Power of Juju", "Tak and the Power of Juju (USA).rvz",
            "ac9b16004e7a8eb87e5acebb5c095541ace72e18", 1459978240),
        new("007 - Agent Under Fire", "007 - Agent Under Fire (USA) (Rev 1).rvz", "7c8a0148f0e5d5b1f40bdfb098eba1935df1932e", 1459978240),
        new("007 - Everything or Nothing", "007 - Everything or Nothing (USA).rvz", "850fc473afcca94171864e754a098a81d9276e98", 1459978240),
        new("007 - From Russia with Love", "007 - From Russia with Love (USA).rvz", "0f002bca477a6631cc90e909b39f53fc27e60855", 1459978240),
        new("007 - Nightfire", "007 - Nightfire (USA).rvz", "7275e43f04caa3c9ec6f3675a54d043d4c544bf3", 1459978240),
        new("1080 Avalanche", "1080 Avalanche (USA).rvz", "09b059208054434df2dadf20b96292b8e11fa908", 1459978240),
        new("18 Wheeler - American Pro Trucker", "18 Wheeler - American Pro Trucker (USA).rvz", "5fea37dc1e9b4e7112f14e81bfda67e38410bd11", 1459978240),
        new("2002 FIFA World Cup", "2002 FIFA World Cup (USA).rvz", "0e4e9ad320de2502a2405548c1cc691720b591aa", 1459978240),
        new("4x4 Evo 2", "4x4 Evo 2 (USA).rvz", "7a384b777c3bc06ca67f0707e4b610831024f307", 1459978240),
        new("Aggressive Inline", "Aggressive Inline (USA).rvz", "5df120bab0042b81b8bbd01774ed9318cecec62f", 1459978240),
        new("Alien Hominid", "Alien Hominid (USA).rvz", "227ba514263da3d51e2288672414ad496507a77d", 1459978240),
        new("All-Star Baseball 2002", "All-Star Baseball 2002 (USA).rvz", "074f624da00da21216c14b9086efac9b22168254", 1459978240),
        new("All-Star Baseball 2003 featuring Derek Jeter", "All-Star Baseball 2003 featuring Derek Jeter (USA).rvz", "c16946c178cbf38add8cebd3a31e9b9cc8815c25", 1459978240),
        new("All-Star Baseball 2004 featuring Derek Jeter", "All-Star Baseball 2004 featuring Derek Jeter (USA).rvz", "72130d8dfba37ef562ea758bab5a55420e3858f8", 1459978240),
        new("Amazing Island", "Amazing Island (USA).rvz", "b82cb04d83b9448e63b594409b378302772c5e38", 1459978240),
        new("American Chopper 2 - Full Throttle", "American Chopper 2 - Full Throttle (USA).rvz", "a56283207bf046faf262afee1278605834329390", 1459978240),
        new("Animaniacs - The Great Edgar Hunt", "Animaniacs - The Great Edgar Hunt (USA).rvz", "b21bf13c0d297ab350edbdc80d682e054328a743", 1459978240),
        new("Ant Bully, The", "Ant Bully, The (USA) (En,Fr).rvz", "2f55464b07983a4e89ce5d360339a73ef106320b", 1459978240),
        new("Aquaman - Battle for Atlantis", "Aquaman - Battle for Atlantis (USA).rvz", "547231c50378665ac2e4a6b3045b1381a96898be", 1459978240),
        new("Army Men - Air Combat - The Elite Missions", "Army Men - Air Combat - The Elite Missions (USA).rvz", "1fadea0db0547efe014d4dd8030afd32b7230b1c", 1459978240),
        new("Army Men - RTS", "Army Men - RTS (USA).rvz", "6a17e1535a4a40d84f7737bcad4fa5599a1f3a14", 1459978240),
        new("Army Men - Sarge's War", "Army Men - Sarge's War (USA).rvz", "21ab1185a4dec9c40e1999500ed9bca7d99eddb3", 1459978240),
        new("ATV - Quad Power Racing 2", "ATV - Quad Power Racing 2 (USA).rvz", "f9c6a0bdd336d0e3a3d48b8d25874f514635f37f", 1459978240),
        new("Auto Modellista", "Auto Modellista (USA).rvz", "800b74412f60939c2d93d7f1ce679cd3da068e55", 1459978240),
        new("Backyard Baseball", "Backyard Baseball (USA).rvz", "25477c45eef85de8058635353c74f829512e2826", 1459978240),
        new("Backyard Football", "Backyard Football (USA).rvz", "0aecb7fd86ec9994bc1a440796536fbac733c1b4", 1459978240),
        new("Backyard Sports - Baseball 2007", "Backyard Sports - Baseball 2007 (USA).rvz", "4e2a0c9458984d01cbbd41540296236f231ba33b", 1459978240),
        new("Bad Boys - Miami Takedown", "Bad Boys - Miami Takedown (USA).rvz", "6960d88596dfe89e5da516c70ac2700b8089d38f", 1459978240),
        new("Baldur's Gate - Dark Alliance", "Baldur's Gate - Dark Alliance (USA).rvz", "c2d49e31143dbb4818b9f9198446618e6265190e", 1459978240),
        new("Baten Kaitos - Eternal Wings and the Lost Ocean", "Baten Kaitos - Eternal Wings and the Lost Ocean (USA) (Disc 1).rvz", "0ce23b9b41bee99e475812b46e236c89a0987956", 1459978240),
        new("Baten Kaitos - Eternal Wings and the Lost Ocean", "Baten Kaitos - Eternal Wings and the Lost Ocean (USA) (Disc 2).rvz", "a2a4b75e7866d9272407da95ec93986bef4c7dfc", 1459978240)
    ];

    /// <summary>The Wii catalog (45 games with their No-Intro SHA-1 and ISO size).</summary>
    public static readonly RealRvz[] Wii =
    [
        new("Big Brain Academy - Wii Degree", "Big Brain Academy - Wii Degree (USA) (En,Fr,Es).rvz",
            "37896d2a60172695467d911e1d77d02f846a9856", 4699979776),
        new("Cabela's Monster Buck Hunter", "Cabela's Monster Buck Hunter (USA).rvz",
            "5961af841561e95c9c48778a53abb59f2036fe1f", 4699979776),
        new("Deadly Creatures", "Deadly Creatures (USA) (En,Fr,Es).rvz",
            "c75f7b0f0ba13626ddab9412e2b6bb71ea4b584e", 4699979776),
        new("DreamWorks Kung Fu Panda", "DreamWorks Kung Fu Panda (USA) (En,Fr).rvz",
            "b92744a9eff56631bf654e5f4d003ecdb464581a", 4699979776),
        new("Guitar Hero - Aerosmith", "Guitar Hero - Aerosmith (USA) (En,Fr).rvz",
            "b95dab2697b4f0571f5512b3fcce1a5a75e021eb", 4699979776),
        new("Iron Man (Rev 1)", "Iron Man (USA) (En,Fr,Es) (Rev 1).rvz",
            "fbab7486ae979e7937a2a74337a9f77121e61e22", 4699979776),
        new("Just Dance 2014", "Just Dance 2014 (USA) (En,Fr,Es).rvz",
            "5200b41d771c3f5f71fdf45752e27d44d3cffb64", 4699979776),
        new("KidFit Island Resort", "KidFit Island Resort (USA).rvz",
            "f6b63c18a1fac23fe535e4e98d4f5b0e1dcfac6f", 4699979776),
        new("Mountain Sports", "Mountain Sports (USA) (En,Fr).rvz",
            "17f888209833e3b36f86ec963e9d85b506a7507e", 4699979776),
        new("NASCAR Kart Racing", "NASCAR Kart Racing (USA).rvz",
            "998b8829e421ec33785311bd7932667bcb5dd08e", 4699979776),
        new("Nickelodeon SpongeBob's Boating Bash", "Nickelodeon SpongeBob's Boating Bash (USA).rvz",
            "fbabb5b292f9f5918dcc21cc29e1b7d384b3633f", 4699979776),
        new("Resident Evil - The Umbrella Chronicles", "Resident Evil - The Umbrella Chronicles (USA).rvz",
            "ae52bca6e1a0bf90d8e91cb62fbfba7a9ba750f7", 4699979776),
        new("Rig Racer 2", "Rig Racer 2 (USA).rvz",
            "924bf0e0e9827c7436245fe0feb4c01d852165e0", 4699979776),
        new("Smurfs 2, The", "Smurfs 2, The (USA) (En,Fr,Es).rvz",
            "ac13785a09a4ad45d5b7a741061cdc1a501caff6", 4699979776),
        new("Wii Fit Plus", "Wii Fit Plus (USA) (En,Fr,Es).rvz",
            "5b9c83266681293f16dafba0cfe5ac5775df0330", 4699979776),
        new("$1,000,000 Pyramid, The", "$1,000,000 Pyramid, The (USA).rvz", "43724fdd3f0dbdbf23dcad2b3036749b53de173b", 4699979776),
        new("007 - Quantum of Solace", "007 - Quantum of Solace (USA) (En,Fr).rvz", "dc4e3fb3c06f5a652076efb36c0bbb9dd3b7c447", 4699979776),
        new("10 Minute Solution", "10 Minute Solution (USA) (En,Fr).rvz", "fdfc0a9dff00c829f61becab9097c3d143db9bd7", 4699979776),
        new("101-in-1 Party Megamix", "101-in-1 Party Megamix (USA) (En,Fr,Es).rvz", "69c8eb01482c5e58a0e0a07fa202b19aa3cd87c3", 4699979776),
        new("101-in-1 Sports Party Megamix", "101-in-1 Sports Party Megamix (USA) (En,Fr,Es).rvz", "4db8fa273b0584e5052f18b02f7e44c1762350ff", 4699979776),
        new("2010 FIFA World Cup South Africa", "2010 FIFA World Cup South Africa (USA) (En,Es).rvz", "92c5a9312f2ca95a49588a7571d28d8c1e71e711", 4699979776),
        new("ABBA - You Can Dance", "ABBA - You Can Dance (USA) (En,Fr,Es).rvz", "1fb886a3afffd0fb1690be80e5d62bc98ddce02d", 4699979776),
        new("ABC Wipeout - Create & Crash", "ABC Wipeout - Create & Crash (USA).rvz", "4b349dab473e2aa0ac1d07b82136bd53bd807b3d", 4699979776),
        new("ABC Wipeout - The Game", "ABC Wipeout - The Game (USA).rvz", "3f67f62dce08af87d137abafbdf156a1fd3cd3c1", 4699979776),
        new("ABC Wipeout 2", "ABC Wipeout 2 (USA).rvz", "12d9b729d73a95ef37df6a4b6072daec0b7325bf", 4699979776),
        new("ABC Wipeout 3", "ABC Wipeout 3 (USA).rvz", "6034997894f8fbdef6f0ac084295b9310e2244af", 4699979776),
        new("AC-DC Live - Rock Band Track Pack", "AC-DC Live - Rock Band Track Pack (USA).rvz", "e346f267b1f0646f66559ab6caf95a108a8811b1", 4699979776),
        new("Academy of Champions - Soccer", "Academy of Champions - Soccer (USA) (En,Fr,Es).rvz", "ee63539b810aab46df1a3a26f4b92d67a8919b47", 4699979776),
        new("Action Girlz Racing", "Action Girlz Racing (USA).rvz", "5ca9617754e8b2e071d5ba39ce7f764be8ed95eb", 4699979776),
        new("Active Life - Explorer", "Active Life - Explorer (USA) (En,Fr,Es).rvz", "023d5b239468db827eac6532f58c220848b1e99f", 4699979776),
        new("Active Life - Extreme Challenge", "Active Life - Extreme Challenge (USA).rvz", "119be7c51f21a6a1c34f6c9322e6b7b36529e64a", 4699979776),
        new("Active Life - Magical Carnival", "Active Life - Magical Carnival (USA) (En,Fr,Es).rvz", "75f17c82e6b5a7a92bf046122dc9f844067a9f6d", 4699979776),
        new("Active Life - Outdoor Challenge", "Active Life - Outdoor Challenge (USA) (En,Fr).rvz", "639f6ab44f3d7e569845766e14044d4312de091d", 4699979776),
        new("Activision Demo Action Pack", "Activision Demo Action Pack (USA).rvz", "b5d07f79214ccf1f556bef0bb978850423e9e45d", 4699979776),
        new("Adventures of Tintin, The - The Game", "Adventures of Tintin, The - The Game (USA) (En,Fr,Es,Pt).rvz", "52503172fd6d719f5bd68f6e88cb7a2fab190213", 4699979776),
        new("Agatha Christie - And Then There Were None", "Agatha Christie - And Then There Were None (USA).rvz", "34902d2c79a35fa553ad376dcf8e531a7a1d7a59", 4699979776),
        new("Agatha Christie - Evil Under the Sun", "Agatha Christie - Evil Under the Sun (USA) (En,Fr,Es).rvz", "5dc5235275352915cde561e2d70187496c9d5f8c", 4699979776),
        new("Aladdin Magic Racer", "Aladdin Magic Racer (USA).rvz", "38f585d51940f232d82defc232cc9172edbfc94a", 4699979776),
        new("Alien Monster Bowling League", "Alien Monster Bowling League (USA).rvz", "44fb244c9757dc7698325fffd9ab68e3c6ae376d", 4699979776),
        new("Alien Syndrome", "Alien Syndrome (USA).rvz", "b527073e130d64a81654221559c80e8dafae4e95", 4699979776),
        new("Aliens in the Attic", "Aliens in the Attic (USA) (En,Es).rvz", "67377e1dc50200df932aa04d4fe7b01438f7084f", 4699979776),
        new("All Star Cheer Squad", "All Star Cheer Squad (USA).rvz", "38d7454e413dbf7847a24bb082e826c9a124cf0e", 4699979776),
        new("All Star Cheer Squad 2", "All Star Cheer Squad 2 (USA).rvz", "456af520a62d13246512ca79aef926b9d2ad83dc", 4699979776),
        new("All Star Karate", "All Star Karate (USA) (En,Fr,Es).rvz", "e9411f5a3b93a9db40e0cc0f81f230c6c53a8c89", 4699979776),
        new("Alone in the Dark", "Alone in the Dark (USA) (En,Fr,Es) (Rev 1).rvz", "97314fd611475cb6530e6d386fc3124efc426e23", 4699979776)
    ];
}

/// <summary>Shared helpers for tests that run only when a real RVZ file is present.</summary>
public static class RealRvzOnly
{
    /// <summary>Full path of the catalog entry in <paramref name="dir"/>, or null when absent.</summary>
    public static string? PathIfPresent(RealRvzCatalog.RealRvz entry, string dir)
    {
        var path = Path.Combine(dir, entry.File);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Streaming SHA-1 of the entire decoded disc image.</summary>
    public static string Sha1(RvzReader reader)
    {
        using var sha = SHA1.Create();
        var buffer = new byte[1 << 20];
        var pos = 0L;
        while (pos < reader.Length)
        {
            var read = reader.ReadAt(pos, buffer);
            Assert.True(read > 0, $"Decode stopped at offset 0x{pos:X} (image is {reader.Length} bytes).");
            sha.TransformBlock(buffer, 0, read, null, 0);
            pos += read;
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}

/// <summary>
/// Category 1 — decode: each real RVZ file decodes byte-for-byte to its official No-Intro
/// SHA-1 and the expected ISO size. This is the strongest real-world proof the reader
/// reproduces actual game images exactly.
/// </summary>
public class RealRvzDecodeTests
{
    /// <summary>The decode theory data: every catalog file with its directory and No-Intro SHA-1.</summary>
    /// <returns>One row (directory, file, expected SHA-1) per catalog entry.</returns>
    public static TheoryData<string, string, string> Files()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var e in RealRvzCatalog.GameCube) data.Add(RealRvzCatalog.GcDir, e.File, e.Sha1);
        foreach (var e in RealRvzCatalog.Wii) data.Add(RealRvzCatalog.WiiDir, e.File, e.Sha1);
        return data;
    }

    /// <summary>
    /// Guard against silently shrinking coverage: when the game drive is mounted, ALL
    /// catalog files must be present (a typo'd catalog entry would otherwise make every
    /// dependent test a green no-op).
    /// </summary>
    [Fact]
    public void CatalogIsFullyPresent_WhenDriveIsMounted()
    {
        Assert.Equal(45, RealRvzCatalog.GameCube.Length);
        Assert.Equal(45, RealRvzCatalog.Wii.Length);

        var missing = new List<string>();
        foreach (var e in RealRvzCatalog.GameCube)
        {
            var path = Path.Combine(RealRvzCatalog.GcDir, e.File);
            if (File.Exists(path) && Directory.Exists(RealRvzCatalog.GcDir))
            {
                continue;
            }

            if (Directory.Exists(RealRvzCatalog.GcDir)) missing.Add(path);
        }

        foreach (var e in RealRvzCatalog.Wii)
        {
            var path = Path.Combine(RealRvzCatalog.WiiDir, e.File);
            if (File.Exists(path) && Directory.Exists(RealRvzCatalog.WiiDir))
            {
                continue;
            }

            if (Directory.Exists(RealRvzCatalog.WiiDir)) missing.Add(path);
        }

        // The drive is absent on machines without the games — the per-file tests no-op
        // there, so this guard does too. When the drive IS mounted, nothing may be missing.
        if (Directory.Exists(RealRvzCatalog.GcDir) ||
            Directory.Exists(RealRvzCatalog.WiiDir))
        {
            Assert.Empty(missing);
        }
    }

    /// <summary>Verifies that a real file decodes byte-for-byte to its official No-Intro SHA-1.</summary>
    /// <param name="dir">The catalog directory (GameCube or Wii).</param>
    /// <param name="file">The RVZ file name.</param>
    /// <param name="expectedSha1">The expected SHA-1 of the decoded ISO.</param>
    [Theory]
    [MemberData(nameof(Files))]
    public void DecodesToExpectedNoIntroSha1(string dir, string file, string expectedSha1)
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) return;

        using var fs = File.OpenRead(path);
        using var reader = RvzReader.Open(fs, leaveOpen: true);
        Assert.Equal(expectedSha1, RealRvzOnly.Sha1(reader));
    }

    /// <summary>Verifies that the reader reports the official ISO size of a real file.</summary>
    /// <param name="dir">The catalog directory (GameCube or Wii).</param>
    /// <param name="file">The RVZ file name.</param>
    /// <param name="_">Unused; kept so both theories share one member-data shape.</param>
    [Theory]
    [MemberData(nameof(Files))]
    public void ReportsExpectedIsoSize(string dir, string file, string _)
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) return;

        using var reader = RvzReader.Open(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), leaveOpen: false);
        Assert.Equal(FindSize(dir, file), reader.Length);
    }

    private static long FindSize(string dir, string file)
    {
        var catalog = string.Equals(dir, RealRvzCatalog.GcDir, StringComparison.OrdinalIgnoreCase)
            ? RealRvzCatalog.GameCube
            : RealRvzCatalog.Wii;
        foreach (var e in catalog)
        {
            if (e.File == file)
            {
                return e.IsoSize;
            }
        }

        return -1;
    }
}

/// <summary>
/// Category 2 — container structure: the parsed file head / disc struct on real files is
/// self-consistent (RVZ magic, current version, declared physical file size, a valid RVZ
/// compression method, a legal chunk size, and a populated group table).
/// </summary>
public class RealRvzStructureTests
{
    /// <summary>The GameCube structure theory data (directory, file).</summary>
    /// <returns>One row per GameCube catalog entry.</returns>
    public static TheoryData<string, string> GcFiles()
    {
        var data = new TheoryData<string, string>();
        foreach (var e in RealRvzCatalog.GameCube) data.Add(RealRvzCatalog.GcDir, e.File);
        return data;
    }

    /// <summary>The Wii structure theory data (directory, file).</summary>
    /// <returns>One row per Wii catalog entry.</returns>
    public static TheoryData<string, string> WiiFiles()
    {
        var data = new TheoryData<string, string>();
        foreach (var e in RealRvzCatalog.Wii) data.Add(RealRvzCatalog.WiiDir, e.File);
        return data;
    }

    /// <summary>Verifies that the parsed file head and disc struct of a real RVZ are self-consistent.</summary>
    /// <param name="dir">The catalog directory (GameCube or Wii).</param>
    /// <param name="file">The RVZ file name.</param>
    [Theory]
    [MemberData(nameof(GcFiles))]
    [MemberData(nameof(WiiFiles))]
    public void HeaderAndDiscAreValid(string dir, string file)
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) return;

        using var fs = File.OpenRead(path);
        using var reader = RvzReader.Open(fs, leaveOpen: true);

        Assert.True(reader.FileHead.IsRvz, "expected an RVZ file");
        Assert.Equal(WiaFileHead.ImplementedVersion, reader.FileHead.Version);
        Assert.Equal((ulong)fs.Length, reader.FileHead.RvzFileSize);

        Assert.NotEqual(CompressionType.Purge, reader.Disc.Compression);
        Assert.Contains(reader.Disc.Compression, new[]
        {
            CompressionType.None, CompressionType.Bzip2, CompressionType.Lzma,
            CompressionType.Lzma2, CompressionType.Zstd
        });

        var chunk = reader.Disc.ChunkSize;
        var pow2 = (chunk & (chunk - 1)) == 0;
        Assert.True(pow2 || chunk % WiaDisc.GroupSize == 0,
            $"chunk size {chunk} is not a legal RVZ chunk size");
        Assert.Equal((int)chunk, reader.BlockSize);

        Assert.True(reader.GroupEntries.Length > 0, "group table is empty");
    }
}

/// <summary>
/// Category 3 — random access: <c>ReadAt</c> across chunk boundaries matches the streaming
/// full read, and out-of-range reads are clamped. Runs on a couple of representative files.
/// </summary>
public class RealRvzRegionTests
{
    /// <summary>Verifies that a full read of a real GameCube file matches its No-Intro SHA-1.</summary>
    [Fact]
    public void ReadFully_GameCube_MatchesCatalogSha1()
    {
        var e = RealRvzCatalog.GameCube[^1]; // Tak and the Power of Juju
        var path = RealRvzOnly.PathIfPresent(e, RealRvzCatalog.GcDir);
        if (path is null) return;

        using var fs = File.OpenRead(path);
        using var reader = RvzReader.Open(fs, leaveOpen: true);
        var full = reader.ReadFully(); // GameCube image (~1.4 GiB) fits in a byte[].
        Assert.Equal(e.Sha1, Convert.ToHexString(SHA1.HashData(full)).ToLowerInvariant());
    }

    /// <summary>Verifies that ReadAt across chunk boundaries matches the full read of a real GameCube file.</summary>
    [Fact]
    public void ReadAtAroundChunkBoundaries_GameCube_MatchesFullRead()
    {
        var e = RealRvzCatalog.GameCube[12]; // Robots
        var path = RealRvzOnly.PathIfPresent(e, RealRvzCatalog.GcDir);
        if (path is null) return;

        using var fs = File.OpenRead(path);
        using var reader = RvzReader.Open(fs, leaveOpen: true);
        var full = reader.ReadFully();

        var chunk = reader.BlockSize;
        long[] offsets = [0, chunk - 17, chunk, chunk + 5, 2 * chunk - 3, 7 * chunk + 11];
        foreach (var off in offsets)
        {
            if (off >= full.Length) continue;

            var count = (int)Math.Min(4096, full.Length - off);
            var expected = full.AsSpan((int)off, count).ToArray();
            var actual = new byte[count];
            Assert.Equal(count, reader.ReadAt(off, actual));
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>Verifies that out-of-range ReadAt on a real Wii file returns zero bytes.</summary>
    [Fact]
    public void ReadAt_OutOfRange_ReturnsZero_Wii()
    {
        var e = RealRvzCatalog.Wii[^1]; // Wii Fit Plus
        var path = RealRvzOnly.PathIfPresent(e, RealRvzCatalog.WiiDir);
        if (path is null) return;

        using var fs = File.OpenRead(path);
        using var reader = RvzReader.Open(fs, leaveOpen: true);
        var buffer = new byte[16];
        Assert.Equal(0, reader.ReadAt(reader.Length, buffer));
        Assert.Equal(0, reader.ReadAt(reader.Length + 100, buffer));
    }
}

/// <summary>
/// Category 4 — writer round-trip: re-encode a REAL RVZ file back to RVZ with the default
/// options, then decode the output; its SHA-1 must equal the original No-Intro hash. This
/// exercises the writer + reader on real disc data (raw data, decrypted Wii partitions,
/// hash trees, packing) end-to-end.
/// </summary>
public class RealRvzWriteRoundTripTests
{
    /// <summary>Verifies that a real GameCube file re-encodes to RVZ and decodes back to the same SHA-1.</summary>
    [Fact]
    public void RealGameCube_ReencodedToRvz_DecodesBackToSameSha1()
    {
        var e = RealRvzCatalog.GameCube[^1]; // Tak and the Power of Juju
        var path = RealRvzOnly.PathIfPresent(e, RealRvzCatalog.GcDir);
        if (path is null) return;

        ReencodeAndVerifySha1(path, e.Sha1);
    }

    /// <summary>Verifies that a real Wii file re-encodes to RVZ and decodes back to the same SHA-1.</summary>
    [Fact]
    public void RealWii_ReencodedToRvz_DecodesBackToSameSha1()
    {
        var e = RealRvzCatalog.Wii[9]; // NASCAR Kart Racing
        var path = RealRvzOnly.PathIfPresent(e, RealRvzCatalog.WiiDir);
        if (path is null) return;

        ReencodeAndVerifySha1(path, e.Sha1);
    }

    private static void ReencodeAndVerifySha1(string path, string expectedSha1)
    {
        // A dedicated temp directory that is always removed, even when the test fails: the
        // re-encoded file can be over a gigabyte, so it must never linger in %TEMP%.
        var directory = Directory.CreateTempSubdirectory("rvzsharp-roundtrip-").FullName;
        try
        {
            using var decoded = Blob.Open(path);
            var filename = Path.Combine(directory, "reencoded.rvz");
            using (var outFile = File.Create(filename))
            {
                RvzWriter.Write(decoded, outFile, RvzWriteOptions.Default);
            }

            using var fs = File.OpenRead(filename);
            using var reader = RvzReader.Open(fs, leaveOpen: true);
            Assert.Equal(expectedSha1, RealRvzOnly.Sha1(reader));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
