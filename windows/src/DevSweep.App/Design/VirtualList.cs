using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Design;

/// <summary>
/// A long list of fixed-height rows that only builds the rows on screen (plus a few above and below). Thousands of items scroll as
/// smoothly as ten: scrolling builds a handful of rows and drops the ones that left. It owns its scroll bar. Built in code, with no
/// WinRT collection crossing into XAML, so it is safe under Native AOT.
/// </summary>
internal sealed class VirtualList
{
    private const int Buffer = 4;
    private readonly ScrollViewer _scroll;
    private readonly Grid _host;
    private readonly double _rowHeight;
    private readonly Func<int, UIElement> _build;
    private readonly Dictionary<int, UIElement> _live = [];
    private int _count;

    public VirtualList(double rowHeight, Func<int, UIElement> build, string automationName)
    {
        ArgumentNullException.ThrowIfNull(build);
        _rowHeight = rowHeight;
        _build = build;
        _host = new Grid { VerticalAlignment = VerticalAlignment.Top };
        _scroll = new ScrollViewer
        {
            Content = _host,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsTabStop = false,
        };
        AutomationProperties.SetName(_scroll, automationName);
        _scroll.ViewChanged += (_, _) => Update();
        _scroll.SizeChanged += (_, _) => Update();
    }

    public FrameworkElement Root => _scroll;

    /// <summary>How many items the list has (rows exist only for the ones on screen).</summary>
    public int Count => _count;

    /// <summary>How many rows are built right now; far fewer than <see cref="Count"/> for a long list.</summary>
    public int RealizedCount => _live.Count;

    /// <summary>Sets the number of items and rebuilds the rows on screen. <paramref name="toTop"/> scrolls back to the first item.</summary>
    public void SetCount(int count, bool toTop = false)
    {
        _count = Math.Max(0, count);
        _host.Height = _count * _rowHeight;
        _live.Clear();
        _host.Children.Clear();
        if (toTop)
        {
            _scroll.ChangeView(null, 0, null, disableAnimation: true);
        }

        Update();
    }

    /// <summary>Builds the rows on screen again (the items changed but not how many).</summary>
    public void Invalidate() => SetCount(_count);

    /// <summary>The row of item <paramref name="index"/> when it is built (for selection highlights); null when it is off screen.</summary>
    public IEnumerable<UIElement> Realized => _live.Values;

    public UIElement? RowAt(int index) => _live.TryGetValue(index, out UIElement? row) ? row : null;

    public void ScrollTo(int index) => _scroll.ChangeView(null, Math.Max(0, (index * _rowHeight) - (_rowHeight * 2)), null, disableAnimation: true);

    private void Update()
    {
        double viewport = _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : (_scroll.ActualHeight > 0 ? _scroll.ActualHeight : 800);
        int first = Math.Max(0, (int)Math.Floor(_scroll.VerticalOffset / _rowHeight) - Buffer);
        int last = Math.Min(_count - 1, (int)Math.Ceiling((_scroll.VerticalOffset + viewport) / _rowHeight) + Buffer);
        foreach (int index in _live.Keys.Where(i => i < first || i > last).ToList())
        {
            _host.Children.Remove(_live[index]);
            _live.Remove(index);
        }

        for (int i = first; i <= last; i++)
        {
            if (_live.ContainsKey(i))
            {
                continue;
            }

            UIElement row = _build(i);
            if (row is FrameworkElement element)
            {
                element.Height = _rowHeight - 2;
                element.VerticalAlignment = VerticalAlignment.Top;
                element.Margin = new Thickness(0, i * _rowHeight, 0, 0);
            }

            _live[i] = row;
            _host.Children.Add(row);
        }
    }
}
