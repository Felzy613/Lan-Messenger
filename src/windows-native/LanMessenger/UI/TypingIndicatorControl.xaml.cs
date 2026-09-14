using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace LanMessenger.UI;

/// <summary>
/// The three animated dots that stand in for the word "typing…" — the shape
/// every messenger has used since iMessage. The dots swell and brighten in a
/// staggered wave rather than blinking in unison, which is what makes it read
/// as someone at a keyboard instead of a progress spinner.
///
/// Used at three sizes: inside a bubble at the end of the thread, beside the
/// peer's name in the chat header, and in the sidebar row where the message
/// preview normally sits. macOS's <c>TypingDotsView</c> animates on the same
/// numbers so both platforms move identically.
/// </summary>
public sealed partial class TypingIndicatorControl : UserControl
{
    // 0.6 s out, 0.6 s back, each dot a fifth of a second behind the one before
    // it. Same numbers as the macOS view.
    private static readonly TimeSpan PulseDuration = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan Stagger       = TimeSpan.FromMilliseconds(200);

    private readonly Storyboard _storyboard = new();
    private readonly Ellipse[] _dots;
    private readonly ScaleTransform[] _scales;

    private bool _isActive;
    private bool _loaded;
    private bool _useBubble;

    public TypingIndicatorControl()
    {
        InitializeComponent();
        _dots   = [Dot1, Dot2, Dot3];
        _scales = [Dot1Scale, Dot2Scale, Dot3Scale];
        BuildStoryboard();

        Visibility = Visibility.Collapsed;
        // The storyboard can only start once the dots are in the visual tree,
        // and it must not be left ticking on a control that has been recycled
        // out of a list.
        Loaded   += (_, _) => { _loaded = true;  Apply(); };
        Unloaded += (_, _) => { _loaded = false; _storyboard.Stop(); };
    }

    /// <summary>
    /// Whether the peer is typing. Shows or hides the dots and starts or stops
    /// the animation — a storyboard left running behind a collapsed control is
    /// still ticked every frame.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            Apply();
        }
    }

    /// <summary>Dot diameter in pixels. 8 px (the default) inside the thread bubble.</summary>
    public double DotDiameter
    {
        get => Dot1.Width;
        set
        {
            foreach (var dot in _dots)
            {
                dot.Width  = value;
                dot.Height = value;
            }
        }
    }

    /// <summary>Gap between dots, scaled down alongside <see cref="DotDiameter"/>.</summary>
    public double DotSpacing
    {
        get => DotRow.Spacing;
        set => DotRow.Spacing = value;
    }

    /// <summary>Dot colour. Defaults to the secondary text brush from XAML.</summary>
    public Brush DotBrush
    {
        get => Dot1.Fill;
        set { foreach (var dot in _dots) dot.Fill = value; }
    }

    /// <summary>
    /// Wraps the dots in an incoming message bubble, for the copy that sits at
    /// the end of the thread. Uses the same uniform 16 px corner and incoming
    /// fill as <c>MessageBubbleControl</c>, so it reads as the message that is
    /// about to arrive. Re-applied on each activation, which is also how the
    /// bubble picks up a light/dark switch (Theme re-points its brushes and
    /// already-rendered controls keep the old one until their next refresh).
    /// </summary>
    public void UseBubbleShell()
    {
        _useBubble         = true;
        Shell.Background   = Theme.IncomingBubbleBrush;
        Shell.CornerRadius = new CornerRadius(16);
        Shell.Padding      = new Thickness(12, 10, 12, 10);
    }

    private void Apply()
    {
        Visibility = _isActive ? Visibility.Visible : Visibility.Collapsed;
        if (_isActive && _loaded)
        {
            if (_useBubble) Shell.Background = Theme.IncomingBubbleBrush;
            _storyboard.Begin();
        }
        else
        {
            _storyboard.Stop();
        }
    }

    private void BuildStoryboard()
    {
        for (var i = 0; i < _dots.Length; i++)
        {
            var begin = TimeSpan.FromTicks(Stagger.Ticks * i);
            _storyboard.Children.Add(Pulse(_dots[i],   "Opacity", 0.38, 1.00, begin));
            _storyboard.Children.Add(Pulse(_scales[i], "ScaleX",  0.82, 1.18, begin));
            _storyboard.Children.Add(Pulse(_scales[i], "ScaleY",  0.82, 1.18, begin));
        }
    }

    // Targets the objects directly rather than by x:Name: the control is hosted
    // in a ListView footer and in a DataTemplate, both of which have namescopes
    // of their own that name-based targeting would have to resolve against.
    private static DoubleAnimation Pulse(DependencyObject target, string property,
                                         double from, double to, TimeSpan begin)
    {
        var animation = new DoubleAnimation
        {
            From           = from,
            To             = to,
            Duration       = new Duration(PulseDuration),
            BeginTime      = begin,
            AutoReverse    = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            // Opacity and RenderTransform are both independent targets, so this
            // runs off the UI thread and the flag changes nothing about how it
            // is evaluated. It is here only so that a future WinUI judging one
            // of them dependent degrades to "animates on the UI thread" instead
            // of throwing out of a Loaded handler.
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }
}
