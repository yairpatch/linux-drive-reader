using LinuxFs.Core.Btrfs;

namespace LinuxFs.Tests;

public class CompressionTests
{
    // A 1 KB JSON file followed by zeros up to 4096 bytes, compressed as one zstd frame. Some Linux kernels
    // compress a small file's whole block like this while recording only the file's own length.
    private const string PlainJson = "ewogICJtYW5pZmVzdF92ZXJzaW9uIjogMywKICAibmFtZSI6ICJFeGFtcGxlIiwKICAidmVyc2lvbiI6ICIxLjIuMyIsCiAgInBlcm1pc3Npb25zIjogWwogICAgInN0b3JhZ2UiLAogICAgInRhYnMiLAogICAgImFjdGl2ZVRhYiIsCiAgICAic2NyaXB0aW5nIgogIF0sCiAgImJhY2tncm91bmQiOiB7CiAgICAic2VydmljZV93b3JrZXIiOiAiYmFja2dyb3VuZC5qcyIKICB9LAogICJhY3Rpb24iOiB7CiAgICAiZGVmYXVsdF9wb3B1cCI6ICJwb3B1cC9wb3B1cC5odG1sIgogIH0sCiAgImljb25zIjogewogICAgIjE2IjogImljb25zL2ljb24xNi5wbmciLAogICAgIjQ4IjogImljb25zL2ljb240OC5wbmciLAogICAgIjEyOCI6ICJpY29ucy9pY29uMTI4LnBuZyIKICB9LAogICJjb250ZW50X3NjcmlwdHMiOiBbCiAgICB7CiAgICAgICJtYXRjaGVzIjogWwogICAgICAgICJodHRwczovLyouZXhhbXBsZS5jb20vKiIKICAgICAgXSwKICAgICAgImpzIjogWwogICAgICAgICJsaWIvY29udGVudC5qcyIKICAgICAgXSwKICAgICAgInJ1bl9hdCI6ICJkb2N1bWVudF9pZGxlIgogICAgfQogIF0KfQ==";
    private const string PaddedFrame = "KLUv/WAAD4UJADKOLB9QZ+wIuDdhltsYIYgRbIkjjqt1XaqlitjzgcjMZ7WOAJERdaO4cxpUnpHnSJV4o1iCcrClTgSl2Iz/zDbTHvnX/2A71rEQBoRheTEdljosqHnjnkkqOhFG5BuHE61EU5FkLyQ0QLr5IxtStspzoFwWjZMtUDV2ef7RZZsJD/Tj33gm/Ku6sIQ/V1XVIFC2c7qTHQvpSGu3m+j4QGCVbTt+AQVl+6BP2Q/ysxP+2K2qAQU0IEACghRVPcelPp5klxuSDkxbs2pOgkwcsHEJX+MthmQDXaBtlU+CSS3gLIWyAQAYgLLBQAbDlDAesHiH5XCYxLt+hesQCbICYrAko/nvyzShAuzDc2U40UByweUChUlYAKT9FFlXWkUn5xBzZ1agUJJRqAvge8mrGQM=";

    [Fact]
    public void Zstd_keeps_the_wanted_bytes_when_the_frame_holds_more()
    {
        byte[] plain = Convert.FromBase64String(PlainJson), frame = Convert.FromBase64String(PaddedFrame);
        var exact = new byte[plain.Length];
        Assert.Equal(plain.Length, Zstd.Decompress(frame, frame.Length, exact));
        Assert.Equal(plain, exact);

        var whole = new byte[4096];
        Assert.Equal(4096, Zstd.Decompress(frame, frame.Length, whole));
        Assert.Equal(plain, whole[..plain.Length]);
        Assert.All(whole[plain.Length..], b => Assert.Equal(0, b));

        var larger = new byte[6000]; // asking for more than there is returns what there is
        Assert.Equal(4096, Zstd.Decompress(frame, frame.Length, larger));
    }

    [Fact]
    public void Zstd_still_rejects_garbage()
    {
        byte[] frame = Convert.FromBase64String(PaddedFrame);
        for (int i = 8; i < frame.Length; i += 3) frame[i] ^= 0x5A;
        Assert.ThrowsAny<InvalidDataException>(() => Zstd.Decompress(frame, frame.Length, new byte[4096]));
    }
}
