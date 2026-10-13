using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>
/// 支持的桌面输入动作。
/// </summary>
internal enum DesktopInputAction
{
    TypeText,
    PressKey,
    Click,
    MoveMouse,
    Scroll
}

/// <summary>
/// 使用 Windows SendInput 模拟桌面键盘和鼠标操作。
/// </summary>
[SupportedOSPlatform("windows6.1")]
internal static class WindowsInputAutomation
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventWheel = 0x0800;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint MouseEventVirtualDesk = 0x4000;
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkLeftWindows = 0x5B;

    /// <summary>
    /// 将 MCP action 参数转换为受支持的动作。
    /// </summary>
    internal static DesktopInputAction ParseAction(string action) => action.Trim().ToLowerInvariant() switch
    {
        "type_text" => DesktopInputAction.TypeText,
        "press_key" => DesktopInputAction.PressKey,
        "click" => DesktopInputAction.Click,
        "move_mouse" => DesktopInputAction.MoveMouse,
        "scroll" => DesktopInputAction.Scroll,
        _ => throw new ArgumentException("action 必须是 type_text、press_key、click、move_mouse 或 scroll。", nameof(action))
    };

    /// <summary>
    /// 执行键鼠动作，并返回可供 MCP 调用方显示的结果。
    /// </summary>
    public static string Execute(KeyboardMouseInput input)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
            throw new PlatformNotSupportedException("模拟键盘和鼠标需要 Windows 7 或更高版本。");

        return ParseAction(input.Action) switch
        {
            DesktopInputAction.TypeText => TypeText(input.Text),
            DesktopInputAction.PressKey => PressKey(input.Key),
            DesktopInputAction.Click => Click(input.X, input.Y, input.Button),
            DesktopInputAction.MoveMouse => MoveMouse(input.X, input.Y),
            DesktopInputAction.Scroll => Scroll(input.X, input.Y, input.ScrollDelta),
            _ => throw new ArgumentOutOfRangeException(nameof(input))
        };
    }

    private static string TypeText(string? text)
    {
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("type_text 动作需要 text 参数。", nameof(text));
        if (text.Length > 2000) throw new ArgumentException("单次最多输入 2000 个 UTF-16 字符。", nameof(text));
        if (text.Any(char.IsControl))
            throw new ArgumentException("text 不能包含控制字符；请使用 press_key 输入 Enter、Tab 等按键。", nameof(text));

        var inputs = new Input[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = CreateKeyboardInput(text[i], KeyEventUnicode);
            inputs[i * 2 + 1] = CreateKeyboardInput(text[i], KeyEventUnicode | KeyEventKeyUp);
        }
        Send(inputs);
        return $"已向当前焦点窗口输入 {text.Length} 个字符。";
    }

    private static string PressKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("press_key 动作需要 key 参数。", nameof(key));
        var parts = key.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 5 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("key 必须是单个按键或最多 4 个修饰键加 1 个按键，例如 Ctrl+Shift+S。", nameof(key));

        var modifiers = new List<ushort>();
        ushort? keyCode = null;
        foreach (var part in parts)
        {
            if (TryGetModifier(part, out var modifier))
            {
                if (keyCode.HasValue) throw new ArgumentException("修饰键必须写在最后一个按键之前。", nameof(key));
                if (!modifiers.Contains(modifier)) modifiers.Add(modifier);
                continue;
            }

            if (keyCode.HasValue) throw new ArgumentException("key 只能包含一个非修饰按键。", nameof(key));
            var mapped = MapKey(part);
            keyCode = mapped.VirtualKey;
            foreach (var implicitModifier in mapped.Modifiers)
                if (!modifiers.Contains(implicitModifier)) modifiers.Add(implicitModifier);
        }

        if (!keyCode.HasValue) throw new ArgumentException("key 必须包含一个非修饰按键。", nameof(key));
        var inputs = new List<Input>(modifiers.Count * 2 + 2);
        inputs.AddRange(modifiers.Select(modifier => CreateKeyboardInput(modifier, 0)));
        inputs.Add(CreateKeyboardInput(keyCode.Value, 0));
        inputs.Add(CreateKeyboardInput(keyCode.Value, KeyEventKeyUp));
        for (var i = modifiers.Count - 1; i >= 0; i--)
            inputs.Add(CreateKeyboardInput(modifiers[i], KeyEventKeyUp));
        Send(inputs.ToArray());
        return $"已向当前焦点窗口发送按键 {key}。";
    }

    private static string Click(int? x, int? y, string? button)
    {
        var position = GetPosition(x, y);
        var (down, up) = (button ?? "left").Trim().ToLowerInvariant() switch
        {
            "left" => (MouseEventLeftDown, MouseEventLeftUp),
            "right" => (MouseEventRightDown, MouseEventRightUp),
            "middle" => (MouseEventMiddleDown, MouseEventMiddleUp),
            _ => throw new ArgumentException("button 必须是 left、right 或 middle。", nameof(button))
        };

        Send([
            CreateMouseInput(position.X, position.Y, 0, MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk),
            CreateMouseInput(0, 0, 0, down),
            CreateMouseInput(0, 0, 0, up)
        ]);
        return $"已在屏幕坐标 ({x},{y}) 执行 {button ?? "left"} 键单击。";
    }

    private static string MoveMouse(int? x, int? y)
    {
        var position = GetPosition(x, y);
        Send([CreateMouseInput(position.X, position.Y, 0, MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk)]);
        return $"鼠标已移动到屏幕坐标 ({x},{y})。";
    }

    private static string Scroll(int? x, int? y, int? scrollDelta)
    {
        if (!scrollDelta.HasValue || scrollDelta.Value == 0 || Math.Abs((long)scrollDelta.Value) > 1200)
            throw new ArgumentException("scroll 动作需要非零 scrollDelta，范围为 -1200 到 1200。", nameof(scrollDelta));

        var flags = MouseEventWheel;
        var positionX = 0;
        var positionY = 0;
        if (x.HasValue || y.HasValue)
        {
            var position = GetPosition(x, y);
            positionX = position.X;
            positionY = position.Y;
            flags |= MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk;
        }
        Send([CreateMouseInput(positionX, positionY, unchecked((uint)scrollDelta.Value), flags)]);
        return $"已滚动鼠标滚轮 {scrollDelta.Value}。";
    }

    private static (int X, int Y) GetPosition(int? x, int? y)
    {
        if (!x.HasValue || !y.HasValue) throw new ArgumentException("鼠标动作需要同时提供 x 和 y 坐标。");
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("当前 Windows 会话没有可用的桌面显示器。");
        if (x < left || x >= left + width || y < top || y >= top + height)
            throw new ArgumentOutOfRangeException(nameof(x), $"坐标 ({x},{y}) 超出虚拟屏幕范围 [{left},{top}] - [{left + width - 1},{top + height - 1}]。");

        var normalizedX = width == 1 ? 0 : (int)Math.Round((x.Value - left) * 65535d / (width - 1));
        var normalizedY = height == 1 ? 0 : (int)Math.Round((y.Value - top) * 65535d / (height - 1));
        return (normalizedX, normalizedY);
    }

    private static (ushort VirtualKey, IReadOnlyList<ushort> Modifiers) MapKey(string key)
    {
        var trimmed = key.Trim();
        var normalized = trimmed.ToUpperInvariant();
        var namedKeys = new Dictionary<string, ushort>(StringComparer.Ordinal)
        {
            ["ENTER"] = 0x0D, ["RETURN"] = 0x0D, ["TAB"] = 0x09,
            ["ESC"] = 0x1B, ["ESCAPE"] = 0x1B, ["BACKSPACE"] = 0x08,
            ["DELETE"] = 0x2E, ["DEL"] = 0x2E, ["INSERT"] = 0x2D, ["INS"] = 0x2D,
            ["HOME"] = 0x24, ["END"] = 0x23, ["PAGEUP"] = 0x21, ["PGUP"] = 0x21,
            ["PAGEDOWN"] = 0x22, ["PGDN"] = 0x22, ["LEFT"] = 0x25, ["UP"] = 0x26,
            ["RIGHT"] = 0x27, ["DOWN"] = 0x28, ["SPACE"] = 0x20,
            ["CAPSLOCK"] = 0x14, ["NUMLOCK"] = 0x90, ["PRINTSCREEN"] = 0x2C,
            ["PAUSE"] = 0x13, ["BACKTAB"] = 0x09
        };
        if (namedKeys.TryGetValue(normalized, out var named)) return (named, []);
        if (normalized.Length is >= 2 and <= 3 && normalized[0] == 'F' &&
            ushort.TryParse(normalized[1..], out var functionNumber) && functionNumber is >= 1 and <= 24)
            return ((ushort)(0x70 + functionNumber - 1), []);
        if (normalized.Length != 1 || normalized[0] > 0x7F)
            throw new ArgumentException($"不支持的按键名称“{key}”。文字字符请使用 type_text。", nameof(key));

        if (normalized[0] is >= 'A' and <= 'Z') return ((ushort)normalized[0], []);
        if (normalized[0] is >= '0' and <= '9') return ((ushort)normalized[0], []);

        var mapped = VkKeyScan(trimmed[0]);
        if (mapped == -1) throw new ArgumentException($"无法映射按键“{key}”。", nameof(key));
        var virtualKey = (ushort)(mapped & 0xFF);
        var shiftState = (mapped >> 8) & 0xFF;
        var modifiers = new List<ushort>(3);
        if ((shiftState & 1) != 0) modifiers.Add(VkShift);
        if ((shiftState & 2) != 0) modifiers.Add(VkControl);
        if ((shiftState & 4) != 0) modifiers.Add(VkMenu);
        return (virtualKey, modifiers);
    }

    private static bool TryGetModifier(string key, out ushort modifier)
    {
        modifier = key.Trim().ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => VkControl,
            "SHIFT" => VkShift,
            "ALT" => VkMenu,
            "WIN" or "WINDOWS" or "META" => VkLeftWindows,
            _ => 0
        };
        return modifier != 0;
    }

    private static void Send(Input[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            var errorCode = Marshal.GetLastWin32Error();
            var detail = errorCode == 0 ? "可能是目标程序以更高权限运行。" : new Win32Exception(errorCode).Message;
            throw new InvalidOperationException($"Windows 仅接受了 {sent}/{inputs.Length} 个输入事件。{detail}");
        }
    }

    private static Input CreateKeyboardInput(ushort virtualKey, uint flags) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = flags }
        }
    };

    private static Input CreateKeyboardInput(char character, uint flags) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput { ScanCode = character, Flags = flags }
        }
    };

    private static Input CreateMouseInput(int x, int y, uint data, uint flags) => new()
    {
        Type = InputMouse,
        Union = new InputUnion
        {
            Mouse = new MouseInput { X = x, Y = y, MouseData = data, Flags = flags }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScan(char character);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
