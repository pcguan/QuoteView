using System.Windows;
using StockClient.App.Views;

internal static class PanelMouseGestureChecks
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); }

    public static void Run()
    {
        var input = new PanelMouseGestures();
        var start = new Point(20, 20);
        var moved = new Point(28, 20);
        Check(input.Wheel(false, false) == PanelWheelAction.Contract, "plain wheel must keep contract navigation");
        input.RightDown();
        Check(!input.RightWheelUsed, "plain right click must open its menu");
        Check(input.Wheel(false, true) == PanelWheelAction.Shade && input.RightWheelUsed,
            "right wheel must adjust shade and suppress the release/menu even before action dispatch");
        input.RightDown();
        Check(!input.RightWheelUsed, "next ordinary right click must recover its menu");

        input.LeftDown(start);
        Check(!input.TryBeginDrag(new Point(21, 21), true, 4, 4), "holding with slight jitter must not enter native drag");
        Check(input.Wheel(true, false) == PanelWheelAction.Group, "left wheel must switch groups");
        Check(!input.TryBeginDrag(moved, true, 4, 4), "wheel gesture must not turn into dragging after a group resize");
        input.LeftUp();
        Check(input.LeftWheelUsed, "next click must know the preceding click was a wheel gesture, not a double-click hide");
        input.LeftDown(start);
        Check(!input.LeftWheelUsed && input.TryBeginDrag(moved, true, 4, 4), "a new left press must restore normal dragging");
        Check(!input.TryBeginDrag(moved, true, 4, 4), "one press must enter native drag only once");
        input.LeftDown(start);
        Check(!input.TryBeginDrag(moved, false, 4, 4), "released button must not start dragging");
        Check(!input.TryBeginDrag(moved, true, 4, 4), "release must clear the pending drag");

        input.LeftDown(start); input.RightDown();
        Check(input.Wheel(true, true) == PanelWheelAction.Shade, "right button takes priority when both held");
        Check(!input.TryBeginDrag(moved, true, 4, 4), "dual-button wheel must not accidentally drag");
        var keyboard = new PanelMouseGestures();
        Check(keyboard.Wheel(false, false, shift: true) == PanelWheelAction.Shade, "Shift wheel must retain brightness control");
        Check(keyboard.Wheel(false, false, ctrl: true) == PanelWheelAction.Group, "Ctrl wheel must retain group navigation");
        Check(!keyboard.RightWheelUsed && !keyboard.LeftWheelUsed, "keyboard wheel alone must not suppress future clicks");
        Check(keyboard.Wheel(false, false, true, true) == PanelWheelAction.Shade, "original Shift before Ctrl priority must remain");
        Check(keyboard.Wheel(true, false, shift: true) == PanelWheelAction.Group, "explicit left chord wins over keyboard modifier");
        Check(keyboard.Wheel(false, true, ctrl: true) == PanelWheelAction.Shade, "explicit right chord wins over keyboard modifier");
        Check(PanelMouseGestures.ResolveWheel(false, false, true, false) == PanelWheelAction.Shade,
            "hidden panel must accept Shift wheel to restore brightness");
        Console.WriteLine("PASS: keyboard Shift/Ctrl wheel compatibility; panel held-right shade/menu suppression, held-left groups/deferred drag, jitter/release/reset/both-button priority; no desktop input");
    }
}
