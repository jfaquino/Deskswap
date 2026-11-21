using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Deskswap.Core;

namespace Deskswap.Ui.Overview;

/// <summary>What a point in the strip is over.</summary>
/// <param name="IsAdd">Whether it is the <c>+</c> card.</param>
/// <param name="Desktop">The desktop, when it is a desktop card.</param>
internal readonly record struct StripTarget(bool IsAdd, DesktopId Desktop);

/// <summary>
/// The desktop strip at the top of each monitor's overview (overview-ux.md §3). It opens as a row of names; when the
/// pointer reaches it, each name grows into a card showing that desktop (its wallpaper and its windows in miniature)
/// and stays that way until the overview closes. Cards can be dragged sideways to reorder desktops.
/// </summary>
internal sealed class DesktopStrip : Canvas
{
    /// <summary>Height of the collapsed strip, in DIPs.</summary>
    public const double CollapsedHeight = 60;

    private const double TopPadding = 14;
    private const double PillHeight = 30;
    private const double PillGap = 8;
    private const double CardGap = 16;
    private const double LabelGap = 6;
    private const double LabelHeight = 20;
    private const double ExpandMs = 260;
    private const double ShiftMs = 170;
    private const double DragThreshold = 5;

    private static readonly SolidColorBrush ButtonBrush = Frozen(Color.FromArgb(0xB8, 0x20, 0x20, 0x20));
    private static readonly SolidColorBrush CloseHoverBrush = Frozen(Color.FromRgb(0xC4, 0x2B, 0x1C));
    private static readonly SolidColorBrush BadgeBrush = Frozen(Color.FromArgb(0xA0, 0x00, 0x00, 0x00));

    private readonly Theme _theme;
    private readonly OverviewText _text;
    private readonly List<Item> _items = [];
    private OverviewState? _state;
    private OverviewMonitor? _monitor;
    private double _width = 1280;
    private double _cardWidth = 160;
    private double _cardHeight = 90;
    private double _progress;
    private double _progressFrom;
    private double _progressTo;
    private long _progressStart = -1;
    private bool _rendering;
    private StripTarget? _highlight;
    private int _selected = -1;
    private Item? _pressed;
    private Point _pressPoint;
    private bool _reordering;
    private int _insertIndex;
    private double _grabOffset;
    private TextBox? _renameBox;
    private DesktopId _renaming;
    private bool _pendingRebuild;

    public DesktopStrip(Theme theme, OverviewText text)
    {
        _theme = theme;
        _text = text;
        Background = Brushes.Transparent; // Hit-testable, so reaching anywhere along the top expands it.
        VerticalAlignment = VerticalAlignment.Top;
        Height = CollapsedHeight;
    }

    /// <summary>Raised when a desktop card or name is clicked (not dragged).</summary>
    public event EventHandler<DesktopId>? DesktopClicked;

    /// <summary>Raised when <c>+</c> is clicked.</summary>
    public event EventHandler? AddClicked;

    /// <summary>Raised when a desktop's close button or Remove is chosen.</summary>
    public event EventHandler<DesktopId>? RemoveClicked;

    /// <summary>Raised when a desktop card is dropped at a new place: the desktop and its new 1-based number.</summary>
    public event EventHandler<(DesktopId Desktop, int Number)>? DesktopMoved;

    /// <summary>Raised when a rename is committed.</summary>
    public event EventHandler<(DesktopId Desktop, string Name)>? Renamed;

    /// <summary>Raised when editing a name ends, saved or not.</summary>
    public event EventHandler? RenameEnded;

    /// <summary>Raised when the strip starts expanding (the window area moves down at the same time).</summary>
    public event EventHandler? ExpandedChanged;

    /// <summary>Gets a value indicating whether the strip is expanded or expanding.</summary>
    public bool IsExpanded => _progressTo >= 1;

    /// <summary>Gets a value indicating whether a name is being edited.</summary>
    public bool IsRenaming => _renameBox is not null;

    /// <summary>Gets the height of the expanded strip, in DIPs.</summary>
    public double ExpandedHeight => TopPadding + _cardHeight + LabelGap + LabelHeight + 12;

    /// <summary>Gets the height the strip will have once its animation ends, in DIPs.</summary>
    public double TargetHeight => IsExpanded ? ExpandedHeight : CollapsedHeight;

    /// <summary>Gets the size of a desktop card, in DIPs.</summary>
    public Size CardSize => new(_cardWidth, _cardHeight);

    /// <summary>Gets how long expanding takes, so the window area can move in step.</summary>
    public static double ExpandDuration => ExpandMs;

    /// <summary>Gets the target of the keyboard selection.</summary>
    public StripTarget? SelectedTarget => _selected >= 0 && _selected < _items.Count ? _items[_selected].Target : null;

    /// <summary>Gets or sets the keyboard-selected item (desktops, then <c>+</c>), or -1.</summary>
    public int Selected
    {
        get => _selected;
        set
        {
            _selected = value < 0 || _items.Count == 0 ? -1 : Math.Clamp(value, 0, _items.Count - 1);
            Restyle();
        }
    }

    /// <summary>Shows the current desktops.</summary>
    /// <param name="state">The overview state.</param>
    /// <param name="monitor">This strip's monitor.</param>
    /// <param name="width">The monitor width, in DIPs.</param>
    /// <param name="height">The monitor height, in DIPs.</param>
    public void Update(OverviewState state, OverviewMonitor monitor, double width, double height)
    {
        _state = state;
        _monitor = monitor;
        _width = width;
        Width = width;

        double aspect = monitor.Bounds.Width / (double)Math.Max(1, monitor.Bounds.Height);
        _cardHeight = Math.Clamp(height * 0.12, 76, 140);
        _cardWidth = _cardHeight * aspect;
        int count = state.Desktops.Count + (state.CanAddDesktop ? 1 : 0);
        double available = width - 96;
        double needed = (count * _cardWidth) + ((count - 1) * CardGap);
        if (needed > available)
        {
            _cardWidth *= available / needed;
            _cardHeight = _cardWidth / aspect;
        }

        if (IsRenaming || _reordering)
        {
            _pendingRebuild = true; // Rebuilding now would throw away typing or the drag.
            return;
        }

        Rebuild();
    }

    /// <summary>Expands (animated) or collapses (at once, when the overview reopens).</summary>
    /// <param name="expanded">Whether to expand.</param>
    public void SetExpanded(bool expanded)
    {
        double target = expanded ? 1 : 0;
        if (target == _progressTo)
        {
            return;
        }

        _progressTo = target;
        if (!expanded || !SystemParameters.ClientAreaAnimation)
        {
            _progress = target;
            _progressStart = -1;
            Layout();
        }
        else
        {
            _progressFrom = _progress;
            _progressStart = 0;
            EnsureRendering();
        }

        Height = TargetHeight;
        ExpandedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lets go of every card and the state they show (when the overview closes).</summary>
    public void Clear()
    {
        _renameBox = null;
        _pressed = null;
        _reordering = false;
        _pendingRebuild = false;
        _state = null;
        _monitor = null;
        Children.Clear();
        _items.Clear();
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
    }

    /// <summary>Finds the card or name under a point.</summary>
    /// <param name="point">The point, relative to this strip.</param>
    /// <returns>The target, or <see langword="null"/>.</returns>
    public StripTarget? HitTest(Point point)
    {
        foreach (var item in _items)
        {
            var bounds = new Rect(GetLeft(item.Container), GetTop(item.Container), item.Container.Width, item.Container.Height);
            bounds.Inflate(CardGap / 2, 8);
            if (bounds.Contains(point))
            {
                return item.Target;
            }
        }

        return null;
    }

    /// <summary>Highlights a drop target while a window is dragged.</summary>
    /// <param name="target">The target, or <see langword="null"/> for none.</param>
    public void Highlight(StripTarget? target)
    {
        if (_highlight != target)
        {
            _highlight = target;
            Restyle();
        }
    }

    /// <summary>Starts editing a desktop's name in place.</summary>
    /// <param name="desktop">The desktop.</param>
    public void BeginRename(DesktopId desktop)
    {
        if (_items.FirstOrDefault(i => !i.Target.IsAdd && i.Target.Desktop == desktop) is not { Desktop: { } info } item)
        {
            return;
        }

        SetExpanded(true);
        CancelRename();
        _renaming = desktop;
        _renameBox = new TextBox
        {
            Text = info.Title,
            FontFamily = Theme.TextFont,
            FontSize = 13,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MaxLength = 40,
            MinWidth = 120,
        };
        _renameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitRename();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelRename();
                e.Handled = true;
            }
        };
        _renameBox.LostKeyboardFocus += (_, _) => CommitRename();
        item.LabelHost.Child = _renameBox;
        _renameBox.Focus();
        _renameBox.SelectAll();
    }

    /// <summary>Stops editing without saving.</summary>
    public void CancelRename()
    {
        if (_renameBox is null)
        {
            return;
        }

        _renameBox = null;
        Rebuild();
        RenameEnded?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (_pendingRebuild && !IsRenaming && !_reordering)
        {
            // Mouse events can arrive around a layout pass; rebuild the children only once it is over.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                if (_pendingRebuild && !IsRenaming && !_reordering)
                {
                    Rebuild();
                }
            });
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    private static Rect Lerp(Rect a, Rect b, double t) =>
        new(Lerp(a.X, b.X, t), Lerp(a.Y, b.Y, t), Lerp(a.Width, b.Width, t), Lerp(a.Height, b.Height, t));

    /// <summary>Gets a value indicating whether an element is the rename box or inside it.</summary>
    /// <param name="element">The element, e.g. the source of a click.</param>
    /// <returns><see langword="true"/> if it belongs to the rename box.</returns>
    public bool IsInRenameBox(object? element) =>
        _renameBox is { } box && element is Visual visual && (visual == box || box.IsAncestorOf(visual));

    /// <summary>Saves the name being edited, if any, and ends editing.</summary>
    public void CommitRename()
    {
        if (_renameBox is not { } box)
        {
            return;
        }

        _renameBox = null;
        Renamed?.Invoke(this, (_renaming, box.Text.Trim()));
        Rebuild();
        RenameEnded?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ building

    private void Rebuild()
    {
        _pendingRebuild = false;
        Children.Clear();
        _items.Clear();
        if (_state is null || _monitor is null)
        {
            return;
        }

        foreach (var desktop in _state.Desktops)
        {
            _items.Add(CreateDesktopItem(desktop));
        }

        if (_state.CanAddDesktop)
        {
            _items.Add(CreateAddItem());
        }

        if (_selected >= _items.Count)
        {
            _selected = _items.Count - 1;
        }

        Layout();
        Restyle();
    }

    private Item CreateDesktopItem(OverviewDesktop desktop)
    {
        bool here = desktop.Id == _monitor!.Shown;

        // Collapsed face: the name, and where else the desktop is shown.
        var pill = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        pill.Children.Add(new TextBlock { Text = desktop.Title, FontFamily = Theme.TextFont, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        if (desktop.ShownOn is { } other && other != _monitor.Id)
        {
            pill.Children.Add(new TextBlock { Text = "", FontFamily = Theme.IconFont, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 });
        }

        // Expanded face: the desktop in miniature.
        var preview = CreatePreview(desktop);
        var overlay = new Grid();
        if (desktop.ShownOn is { } shownOn && shownOn != _monitor.Id)
        {
            overlay.Children.Add(MonitorBadge(shownOn));
        }

        Border? remove = null;
        if (_state!.CanRemoveDesktop)
        {
            remove = CloseButton();
            remove.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                RemoveClicked?.Invoke(this, desktop.Id);
            };
            overlay.Children.Add(remove);
        }

        var item = CreateItem(new StripTarget(false, desktop.Id), desktop, pill, preview, overlay, remove, here);
        item.Label.Text = desktop.Title;
        item.Label.FontWeight = here ? FontWeights.SemiBold : FontWeights.Normal;
        item.LabelHost.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                BeginRename(desktop.Id);
            }
        };

        string state = desktop.ShownOn == _monitor.Id ? " (✓)" : desktop.ShownOn is { } monitor
            ? ", " + string.Format(CultureInfo.CurrentCulture, _text.ShownOn, LabelOf(monitor))
            : string.Empty;
        AutomationProperties.SetName(item.Container, desktop.Title + state);

        var rename = new MenuItem { Header = _text.Rename };
        rename.Click += (_, _) => BeginRename(desktop.Id);
        var removeItem = new MenuItem { Header = _text.Remove, IsEnabled = _state.CanRemoveDesktop };
        removeItem.Click += (_, _) => RemoveClicked?.Invoke(this, desktop.Id);
        item.Container.ContextMenu = new ContextMenu { Items = { rename, removeItem } };
        return item;
    }

    private Item CreateAddItem()
    {
        var pill = Glyph("", 12);
        var face = new Grid { Width = _cardWidth, Height = _cardHeight, Background = _theme.Surface };
        face.Children.Add(Glyph("", 22));
        var item = CreateItem(new StripTarget(true, default), null, pill, face, new Grid(), null, here: false);
        item.Container.ToolTip = _text.NewDesktop;
        AutomationProperties.SetName(item.Container, _text.NewDesktop);
        return item;
    }

    private Item CreateItem(StripTarget target, OverviewDesktop? desktop, FrameworkElement pill, FrameworkElement preview, Grid overlay, Border? remove, bool here)
    {
        // The preview is drawn at card size; the viewbox scales it to whatever size the item has mid-animation.
        var previewBox = new Viewbox { Stretch = Stretch.UniformToFill, Child = preview };
        var face = new Grid { Children = { previewBox, overlay, pill } };
        var clip = new RectangleGeometry();
        var scale = new ScaleTransform(1, 1);
        var shift = new TranslateTransform();
        var container = new Border
        {
            Child = face,
            Cursor = Cursors.Hand,
            Clip = clip,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { scale, shift } },
            SnapsToDevicePixels = true,
        };

        var label = new TextBlock
        {
            FontFamily = Theme.TextFont,
            FontSize = 12.5,
            Foreground = _theme.Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var labelHost = new Border { Child = label, RenderTransform = shift };

        Children.Add(container);
        Children.Add(labelHost);
        var item = new Item(target, desktop, container, clip, scale, shift, pill, previewBox, overlay, remove, label, labelHost)
        {
            PillWidth = MeasureWidth(pill) + 28,
            Here = here,
        };

        container.MouseEnter += (_, _) => SetHover(item, true);
        container.MouseLeave += (_, _) => SetHover(item, false);
        container.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _pressed = item;
            _pressPoint = e.GetPosition(this);
            container.CaptureMouse();
        };
        container.MouseMove += (_, e) => OnItemMove(item, e);
        container.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OnItemRelease(item);
        };
        container.LostMouseCapture += (_, _) =>
        {
            if (_reordering && _pressed == item)
            {
                EndReorder(commit: false);
            }
        };
        return item;
    }

    /// <summary>A desktop in miniature: its monitor's wallpaper with its windows in their places, bottom-most first.</summary>
    private Canvas CreatePreview(OverviewDesktop desktop)
    {
        var monitor = _state!.Monitors.FirstOrDefault(m => m.Id == desktop.PreviewMonitor) ?? _monitor!;
        var canvas = new Canvas { Width = _cardWidth, Height = _cardHeight, ClipToBounds = true };
        var wallpaper = Images.WallpaperView(monitor.Wallpaper, _theme.Backdrop);
        wallpaper.Width = _cardWidth;
        wallpaper.Height = _cardHeight;
        canvas.Children.Add(wallpaper);

        double scale = _cardWidth / Math.Max(1, monitor.Bounds.Width);
        foreach (var window in desktop.Windows)
        {
            var r = window.Bounds;
            double w = r.Width * scale;
            double h = r.Height * scale;
            if (w < 2 || h < 2)
            {
                continue;
            }

            var mini = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(2),
                BorderBrush = _theme.Stroke,
                BorderThickness = new Thickness(0.5),
                Background = _theme.Surface,
                ClipToBounds = true,
            };
            if (window.Image is { } image)
            {
                // Fill and crop (from the top left, where title bars are), never stretch: the window may have changed shape
                // since its picture was taken.
                var picture = new Image { Source = Images.ToBitmap(image), Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
                mini.Child = picture;
            }
            else if (Images.FromIcon(window.Icon) is { } icon)
            {
                double size = Math.Min(20, Math.Min(w, h) * 0.6);
                mini.Child = new Image { Source = icon, Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }

            SetLeft(mini, r.Left * scale);
            SetTop(mini, r.Top * scale);
            canvas.Children.Add(mini);
        }

        return canvas;
    }

    /// <summary>Windows 11 style close button: a dark translucent circle that turns red on hover.</summary>
    private Border CloseButton()
    {
        var button = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = ButtonBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            Opacity = 0,
            Cursor = Cursors.Hand,
            ToolTip = _text.Remove,
            Child = new TextBlock { Text = "", FontFamily = Theme.IconFont, FontSize = 8, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        AutomationProperties.SetName(button, _text.Remove);
        button.MouseEnter += (_, _) => button.Background = CloseHoverBrush;
        button.MouseLeave += (_, _) => button.Background = ButtonBrush;
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        return button;
    }

    private Border MonitorBadge(MonitorId monitor)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = "", FontFamily = Theme.IconFont, FontSize = 10, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = LabelOf(monitor), FontFamily = Theme.TextFont, FontSize = 10.5, Foreground = Brushes.White, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return new Border
        {
            Child = content,
            Background = BadgeBrush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            ToolTip = string.Format(CultureInfo.CurrentCulture, _text.ShownOn, LabelOf(monitor)),
        };
    }

    private string LabelOf(MonitorId monitor) => _state?.Monitors.FirstOrDefault(m => m.Id == monitor)?.Label ?? string.Empty;

    private TextBlock Glyph(string glyph, double size) => new()
    {
        Text = glyph,
        FontFamily = Theme.IconFont,
        FontSize = size,
        Foreground = _theme.Text,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static double MeasureWidth(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }

    // ------------------------------------------------------------------ layout and animation

    /// <summary>Places every item for the current expansion progress: names morph into cards.</summary>
    private void Layout()
    {
        if (_items.Count == 0)
        {
            return;
        }

        double p = _progress;
        double eased = 1 - Math.Pow(1 - p, 3);
        double pillsWidth = _items.Sum(i => i.PillWidth) + (PillGap * (_items.Count - 1));
        double cardsWidth = (_items.Count * _cardWidth) + (CardGap * (_items.Count - 1));
        double pillX = (_width - pillsWidth) / 2;
        double cardX = (_width - cardsWidth) / 2;

        foreach (var item in _items)
        {
            var collapsed = new Rect(pillX, TopPadding, item.PillWidth, PillHeight);
            var expanded = new Rect(cardX, TopPadding, _cardWidth, _cardHeight);
            item.Slot = expanded.X;
            pillX += item.PillWidth + PillGap;
            cardX += _cardWidth + CardGap;

            var r = Lerp(collapsed, expanded, eased);
            SetLeft(item.Container, r.X);
            SetTop(item.Container, r.Y);
            item.Container.Width = r.Width;
            item.Container.Height = r.Height;
            double radius = Lerp(PillHeight / 2, 8, eased);
            item.Container.CornerRadius = new CornerRadius(radius);
            item.Clip.Rect = new Rect(0, 0, r.Width, r.Height);
            item.Clip.RadiusX = item.Clip.RadiusY = radius;

            // The name fades out quickly as the card's picture fades in, so they never muddle together.
            item.Pill.Opacity = Math.Clamp(1 - (p * 2.5), 0, 1);
            item.Preview.Opacity = Math.Clamp((p * 1.6) - 0.3, 0, 1);
            item.Overlay.Opacity = Math.Clamp((p * 2) - 1, 0, 1);

            SetLeft(item.LabelHost, expanded.X);
            SetTop(item.LabelHost, expanded.Bottom + LabelGap + ((1 - eased) * -8));
            item.LabelHost.Width = _cardWidth;
            item.LabelHost.Opacity = Math.Clamp((p * 2) - 1, 0, 1);
            item.LabelHost.IsHitTestVisible = p >= 1;
        }

        Height = Lerp(CollapsedHeight, ExpandedHeight, eased);
    }

    private void EnsureRendering()
    {
        if (!_rendering)
        {
            _rendering = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        bool busy = false;

        if (_progressStart >= 0)
        {
            if (_progressStart == 0)
            {
                _progressStart = now; // Timed from the first frame drawn.
            }

            double t = Math.Clamp(Stopwatch.GetElapsedTime(_progressStart, now).TotalMilliseconds / ExpandMs, 0, 1);
            _progress = Lerp(_progressFrom, _progressTo, t);
            if (t >= 1)
            {
                _progressStart = -1;
            }
            else
            {
                busy = true;
            }

            Layout();
        }

        foreach (var item in _items)
        {
            if (item.ShiftStart < 0)
            {
                continue;
            }

            if (item.ShiftStart == 0)
            {
                item.ShiftStart = now;
            }

            double t = Math.Clamp(Stopwatch.GetElapsedTime(item.ShiftStart, now).TotalMilliseconds / ShiftMs, 0, 1);
            item.Shift.X = Lerp(item.ShiftFrom, item.ShiftTo, 1 - Math.Pow(1 - t, 3));
            if (t >= 1)
            {
                item.ShiftStart = -1;
            }
            else
            {
                busy = true;
            }
        }

        if (!busy)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
    }

    // ------------------------------------------------------------------ hover, selection and highlight

    private void SetHover(Item item, bool hover)
    {
        item.Hover = hover;
        if (item.Remove is { } remove)
        {
            remove.BeginAnimation(OpacityProperty, new DoubleAnimation(hover && IsExpanded ? 1 : 0, TimeSpan.FromMilliseconds(120)));
        }

        if (!_reordering)
        {
            AnimateScale(item, hover && IsExpanded ? 1.04 : 1);
        }

        Restyle();
    }

    private static void AnimateScale(Item item, double scale)
    {
        var animation = new DoubleAnimation(scale, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 120 : 0))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        item.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        item.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    /// <summary>Applies colors for shown/hover/highlight/selection.</summary>
    private void Restyle()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            bool marked = _highlight == item.Target || i == _selected;
            item.Container.Background = item.Here ? _theme.AccentBrush : item.Hover || marked ? _theme.SurfaceHover : _theme.Surface;
            item.Container.BorderBrush = item.Here || marked ? _theme.AccentBrush : _theme.Stroke;
            item.Container.BorderThickness = new Thickness(item.Here || marked ? 2 : 1);
            var foreground = item.Here ? _theme.OnAccent : _theme.Text;
            if (item.Pill is TextBlock glyph)
            {
                glyph.Foreground = foreground;
            }
            else if (item.Pill is Panel panel)
            {
                foreach (var text in panel.Children.OfType<TextBlock>())
                {
                    text.Foreground = foreground;
                }
            }

            if (_highlight == item.Target && !_reordering)
            {
                AnimateScale(item, 1.06);
            }
            else if (!item.Hover && !_reordering)
            {
                AnimateScale(item, 1);
            }
        }
    }

    // ------------------------------------------------------------------ clicking and reordering (macOS: drag a card sideways)

    private void OnItemMove(Item item, MouseEventArgs e)
    {
        if (_pressed != item || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (!_reordering)
        {
            bool canReorder = !item.Target.IsAdd && IsExpanded && _progressStart < 0 && !IsRenaming;
            if (!canReorder || Math.Abs(position.X - _pressPoint.X) < DragThreshold)
            {
                return;
            }

            _reordering = true;
            _insertIndex = _items.IndexOf(item);
            _grabOffset = _pressPoint.X - item.Slot;
            SetZIndex(item.Container, 10);
            SetZIndex(item.LabelHost, 10);
            AnimateScale(item, 1.08);
            item.Remove?.BeginAnimation(OpacityProperty, null);
            if (item.Remove is not null)
            {
                item.Remove.Opacity = 0;
            }
        }

        // The card follows the pointer; the others slide aside to show where it will land.
        int desktops = _items.Count(i => !i.Target.IsAdd);
        double left = position.X - _grabOffset;
        item.ShiftStart = -1;
        item.Shift.X = left - item.Slot;

        double firstSlot = _items[0].Slot;
        int index = Math.Clamp((int)Math.Round((left - firstSlot) / (_cardWidth + CardGap)), 0, desktops - 1);
        if (index != _insertIndex)
        {
            _insertIndex = index;
            int from = _items.IndexOf(item);
            for (int i = 0; i < desktops; i++)
            {
                if (_items[i] == item)
                {
                    continue;
                }

                int slot = i;
                if (from < index && i > from && i <= index)
                {
                    slot = i - 1;
                }
                else if (index < from && i >= index && i < from)
                {
                    slot = i + 1;
                }

                SlideTo(_items[i], (slot - i) * (_cardWidth + CardGap));
            }
        }
    }

    private void OnItemRelease(Item item)
    {
        bool wasPressed = _pressed == item;
        bool wasReordering = _reordering;
        if (wasReordering)
        {
            EndReorder(commit: true);
        }

        _pressed = null;
        item.Container.ReleaseMouseCapture();
        if (!wasPressed || wasReordering)
        {
            return;
        }

        if (item.Target.IsAdd)
        {
            AddClicked?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            DesktopClicked?.Invoke(this, item.Target.Desktop);
        }
    }

    private void EndReorder(bool commit)
    {
        if (_pressed is not { } item)
        {
            _reordering = false;
            return;
        }

        int from = _items.IndexOf(item);
        int to = _insertIndex;
        _reordering = false;
        SetZIndex(item.Container, 0);
        SetZIndex(item.LabelHost, 0);
        AnimateScale(item, item.Hover ? 1.04 : 1);

        if (commit && to != from && item.Desktop is { } desktop)
        {
            // Land in the new slot, then let the model's update rebuild the strip in the new order.
            SlideTo(item, (to - from) * (_cardWidth + CardGap));
            _pendingRebuild = false;
            DesktopMoved?.Invoke(this, (desktop.Id, to + 1));
            return;
        }

        foreach (var other in _items)
        {
            SlideTo(other, 0);
        }

        if (_pendingRebuild)
        {
            Rebuild();
        }
    }

    private void SlideTo(Item item, double x)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            item.Shift.X = x;
            return;
        }

        item.ShiftFrom = item.Shift.X;
        item.ShiftTo = x;
        item.ShiftStart = 0;
        EnsureRendering();
    }

    /// <summary>One desktop (or the <c>+</c>) in the strip: a container that morphs from a name pill into a card.</summary>
    private sealed class Item(
        StripTarget target,
        OverviewDesktop? desktop,
        Border container,
        RectangleGeometry clip,
        ScaleTransform scale,
        TranslateTransform shift,
        FrameworkElement pill,
        FrameworkElement preview,
        Grid overlay,
        Border? remove,
        TextBlock label,
        Border labelHost)
    {
        public StripTarget Target { get; } = target;

        public OverviewDesktop? Desktop { get; } = desktop;

        public Border Container { get; } = container;

        public RectangleGeometry Clip { get; } = clip;

        public ScaleTransform Scale { get; } = scale;

        public TranslateTransform Shift { get; } = shift;

        public FrameworkElement Pill { get; } = pill;

        public FrameworkElement Preview { get; } = preview;

        public Grid Overlay { get; } = overlay;

        public Border? Remove { get; } = remove;

        public TextBlock Label { get; } = label;

        public Border LabelHost { get; } = labelHost;

        public double PillWidth { get; init; }

        public bool Here { get; init; }

        public bool Hover { get; set; }

        /// <summary>Gets or sets the card's x position when expanded and not shifted.</summary>
        public double Slot { get; set; }

        public double ShiftFrom { get; set; }

        public double ShiftTo { get; set; }

        /// <summary>Gets or sets when the shift animation started: -1 idle, 0 on the next frame.</summary>
        public long ShiftStart { get; set; } = -1;
    }
}
