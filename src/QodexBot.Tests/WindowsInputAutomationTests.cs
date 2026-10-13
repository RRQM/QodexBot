using Xunit;

namespace QodexBot.Tests;

public sealed class WindowsInputAutomationTests
{
    [Theory]
    [InlineData("type_text", "TypeText")]
    [InlineData("press_key", "PressKey")]
    [InlineData("click", "Click")]
    [InlineData("move_mouse", "MoveMouse")]
    [InlineData("scroll", "Scroll")]
    [InlineData("  CLICK  ", "Click")]
    public void ParseAction_RecognizesSupportedActions(string action, string expected)
    {
        Assert.Equal(Enum.Parse<DesktopInputAction>(expected), WindowsInputAutomation.ParseAction(action));
    }

    [Fact]
    public void ParseAction_RejectsUnsupportedActionWithAllowedValues()
    {
        var exception = Assert.Throws<ArgumentException>(() => WindowsInputAutomation.ParseAction("launch_process"));

        Assert.Contains("type_text", exception.Message, StringComparison.Ordinal);
        Assert.Contains("press_key", exception.Message, StringComparison.Ordinal);
    }
}
