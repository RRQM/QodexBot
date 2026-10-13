using System.Buffers.Binary;
using Xunit;

namespace QodexBot.Tests;

public sealed class WindowsScreenCaptureTests
{
    [Fact]
    public void CapturePng_ReturnsDecodablePngWithNonzeroDimensions()
    {
        var bytes = WindowsScreenCapture.CapturePng();

        Assert.True(bytes.Length > 32);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));

        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        Assert.True(width > 0);
        Assert.True(height > 0);

        using var stream = new MemoryStream(bytes);
        using var image = System.Drawing.Image.FromStream(stream);
        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
    }

    [Fact]
    public void CaptureProcessWindowPng_UnknownProcessThrowsHelpfulError()
    {
        var processName = $"qodexbot-test-missing-{Guid.NewGuid():N}";

        var exception = Assert.Throws<InvalidOperationException>(
            () => WindowsScreenCapture.CaptureProcessWindowPng(processName));

        Assert.Contains(processName, exception.Message, StringComparison.Ordinal);
    }
}
