using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ModderLords.App;

/// <summary>
/// The line that shows where a dragged row will land. Without it the mod list gave no clue where a drop would go
/// - you found out afterwards, and dropping near a band boundary looked like it had simply done nothing.
///
/// It draws in the adorner layer above the grid, so it costs the grid no layout and cannot be scrolled out of
/// step with the rows it is describing.
/// </summary>
public sealed class InsertionAdorner : Adorner
{
    private readonly Pen _pen;
    private double _y;
    private double _width;

    public InsertionAdorner(UIElement adornedElement, Brush brush) : base(adornedElement)
    {
        IsHitTestVisible = false;
        _pen = new Pen(brush, 2);
        _pen.Freeze();
    }

    /// <summary>Where the line sits, in the adorned element's coordinates. Redraws only when it actually moves.</summary>
    public void MoveTo(double y, double width)
    {
        if (Math.Abs(_y - y) < 0.5 && Math.Abs(_width - width) < 0.5) return;
        _y = y;
        _width = width;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawLine(_pen, new Point(0, _y), new Point(_width, _y));
        // Small caps at each end, so the line reads as an insertion point rather than a row divider.
        dc.DrawLine(_pen, new Point(0, _y - 4), new Point(0, _y + 4));
        dc.DrawLine(_pen, new Point(_width, _y - 4), new Point(_width, _y + 4));
    }
}
