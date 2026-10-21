using System.Windows;

namespace StockClient.App.Views;

internal enum PanelWheelAction { Contract, Group, Shade }

/// <summary>Separates a held-button wheel gesture from click/menu/drag actions.
/// Kept across row rebuilds; no mouse capture or desktop input is required.</summary>
internal sealed class PanelMouseGestures
{
    private Point? _dragOrigin;
    public bool LeftWheelUsed { get; private set; }
    public bool RightWheelUsed { get; private set; }

    public void LeftDown(Point position)
    {
        _dragOrigin = position;
        LeftWheelUsed = false;
    }

    public void LeftUp() => _dragOrigin = null;
    public void RightDown() => RightWheelUsed = false;

    // Explicit mouse chords win; with no button held, retain Shift before Ctrl.
    public static PanelWheelAction ResolveWheel(bool left, bool right, bool shift, bool ctrl) =>
        right ? PanelWheelAction.Shade : left ? PanelWheelAction.Group
            : shift ? PanelWheelAction.Shade : ctrl ? PanelWheelAction.Group : PanelWheelAction.Contract;

    public PanelWheelAction Wheel(bool left, bool right, bool shift = false, bool ctrl = false)
    {
        if (right) RightWheelUsed = true;
        if (left) { LeftWheelUsed = true; _dragOrigin = null; }
        return ResolveWheel(left, right, shift, ctrl);
    }

    public bool TryBeginDrag(Point position, bool leftPressed, double horizontalThreshold, double verticalThreshold)
    {
        if (!leftPressed) { LeftUp(); return false; }
        if (LeftWheelUsed || _dragOrigin is not { } start) return false;
        if (Math.Abs(position.X - start.X) < horizontalThreshold
            && Math.Abs(position.Y - start.Y) < verticalThreshold) return false;
        _dragOrigin = null;
        return true;
    }
}
