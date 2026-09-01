using System.Security.Cryptography;

namespace StockClient.Core.Updates;

/// <summary>
/// The gate every downloaded byte passes before it may become the running exe:
/// a truncated transfer or a CDN challenge/error page must fail HERE with a
/// message naming the cause. Kept in Core and expressed over bytes rather than
/// paths so the checks that historically broke in production (a 977KB stub, an
/// interception page) are directly testable.
/// </summary>
public static class UpdateVerifier
{
    /// <param name="content">The downloaded bytes; must be seekable (the hash
    /// pass rereads it from the start).</param>
    /// <param name="expectedSize">Size from the release manifest, 0 when the
    /// source doesn't report one.</param>
    /// <param name="expectedSha256">Hex SHA-256 from the manifest, empty when
    /// the source doesn't report one.</param>
    public static void Verify(Stream content, long expectedSize, string expectedSha256)
    {
        if (!content.CanSeek)
            throw new ArgumentException("校验需要可定位的流", nameof(content));

        var length = content.Length;
        if (expectedSize > 0 && length != expectedSize)
            throw new InvalidOperationException(
                $"下载不完整：应为 {expectedSize} 字节，实际 {length} 字节"
                + "（网络中断或被中间缓存截断），稍后会自动重试");

        content.Position = 0;
        var head = new byte[2];
        if (content.Read(head, 0, 2) != 2 || head[0] != (byte)'M' || head[1] != (byte)'Z')
            throw new InvalidOperationException(
                "下载内容不是有效的程序文件（可能被网络设备重定向或拦截），稍后会自动重试");

        // The strong check: byte length can be forged by a manifest generated
        // from the same truncated file — the hash cannot.
        if (!string.IsNullOrEmpty(expectedSha256))
        {
            content.Position = 0;
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(content));
            if (!hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "下载文件 SHA-256 校验失败（内容与发布清单不符），已丢弃；稍后会自动重试");
        }
    }
}
