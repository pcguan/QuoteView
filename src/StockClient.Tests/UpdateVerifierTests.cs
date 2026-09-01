using System.Security.Cryptography;
using System.Text;
using StockClient.Core.Updates;
using Xunit;

namespace StockClient.Tests;

/// <summary>
/// The download gate, pinned against the two failures that actually shipped: a
/// truncated exe whose manifest was generated from the truncated bytes, and a
/// network appliance answering the download URL with an HTML page.
/// </summary>
public class UpdateVerifierTests
{
    private static byte[] Exe(int size = 64)
    {
        var bytes = new byte[size];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        for (var i = 2; i < size; i++) bytes[i] = (byte)i;
        return bytes;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public void Full_download_passes_every_check()
    {
        var bytes = Exe();
        UpdateVerifier.Verify(new MemoryStream(bytes), bytes.Length, Sha(bytes));
    }

    [Fact]
    public void Truncated_download_fails_on_size_before_anything_else()
    {
        var bytes = Exe();
        var ex = Assert.Throws<InvalidOperationException>(
            () => UpdateVerifier.Verify(new MemoryStream(bytes), bytes.Length + 1000, Sha(bytes)));
        Assert.Contains("下载不完整", ex.Message);
    }

    [Fact]
    public void Interception_page_fails_even_when_the_manifest_reports_no_size()
    {
        var page = Encoding.UTF8.GetBytes("<html>需要认证才能访问</html>");
        var ex = Assert.Throws<InvalidOperationException>(
            () => UpdateVerifier.Verify(new MemoryStream(page), 0, ""));
        Assert.Contains("不是有效的程序文件", ex.Message);
    }

    [Fact]
    public void Empty_body_cannot_pass_the_header_check()
    {
        Assert.Throws<InvalidOperationException>(
            () => UpdateVerifier.Verify(new MemoryStream(Array.Empty<byte>()), 0, ""));
    }

    [Fact]
    public void Hash_mismatch_fails_a_download_whose_size_is_right()
    {
        // The exact shape of the 977KB incident inverted: size agrees with the
        // manifest, content does not.
        var bytes = Exe();
        var ex = Assert.Throws<InvalidOperationException>(
            () => UpdateVerifier.Verify(new MemoryStream(bytes), bytes.Length, Sha(Exe(128))));
        Assert.Contains("SHA-256", ex.Message);
    }

    [Fact]
    public void Hash_comparison_ignores_case()
    {
        // Manifests have carried both cases; a case-sensitive compare would
        // reject every good download from one of the sources.
        var bytes = Exe();
        UpdateVerifier.Verify(new MemoryStream(bytes), bytes.Length, Sha(bytes).ToUpperInvariant());
    }

    [Fact]
    public void No_hash_in_the_manifest_skips_the_strong_check()
    {
        var bytes = Exe();
        UpdateVerifier.Verify(new MemoryStream(bytes), bytes.Length, "");
    }
}
