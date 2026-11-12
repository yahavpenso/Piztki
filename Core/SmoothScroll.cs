using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Piztki.Core;

/// <summary>
/// Makes every ScrollViewer in the app glide to where the mouse wheel sends it instead of jumping
/// there. One class handler covers the notch, the settings window, and anything added later.
/// </summary>
public static class SmoothScroll
{
    /// <summary>How far one notch of the wheel moves the content, in pixels.</summary>
    const double StepPerNotch = 56;

    /// <summary>Seconds for the remaining distance to shrink to about a third.</summary>
    const double Glide = 0.075;

    sealed class State
    {
        public double Target;
        public double Offset;   // where we last put it, with the fraction ScrollViewer rounds away
        public bool Running;
    }

    static readonly ConditionalWeakTable<ScrollViewer, State> States = new();
    static readonly List<(ScrollViewer view, State state)> Active = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static double _lastFrame;
    static bool _hooked;

    public static void Enable()
    {
        if (_hooked) return;
        _hooked = true;
        // the bubbling event, so the innermost scroller under the mouse gets the wheel first
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnWheel));
    }

    /// <summary>True while any scroller is still gliding (for tests).</summary>
    public static bool Busy => Active.Count > 0;

    static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer view) return;
        if (view.CanContentScroll && view.Content is ItemsPresenter) return; // scrolls by rows, not pixels
        if (view.ScrollableHeight <= 0.5) return;                            // nothing to scroll: let a parent have it

        var state = States.GetOrCreateValue(view);
        double now = view.VerticalOffset;
        // start from where the content really is if something else moved it (ScrollToEnd, a drag...)
        if (!state.Running || Math.Abs(now - state.Offset) > 1.5)
        {
            state.Offset = now;
            state.Target = now;
        }

        double target = Math.Clamp(state.Target - e.Delta / 120.0 * StepPerNotch, 0, view.ScrollableHeight);
        if (Math.Abs(target - state.Offset) < 0.5 && !state.Running) return; // already at the end: pass it on

        state.Target = target;
        e.Handled = true;

        if (!state.Running)
        {
            state.Running = true;
            if (Active.Count == 0)
            {
                _lastFrame = Clock.Elapsed.TotalSeconds;
                CompositionTarget.Rendering += OnFrame;
            }
            Active.Add((view, state));
        }
    }

    static void OnFrame(object? sender, EventArgs e)
    {
        double time = Clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(time - _lastFrame, 0.001, 0.05);
        _lastFrame = time;
        // the same glide at any frame rate
        double share = 1 - Math.Exp(-dt / (Glide * Math.Max(0.3, Fx.Speed)));

        for (int i = Active.Count - 1; i >= 0; i--)
        {
            var (view, state) = Active[i];

            bool movedElsewhere = Math.Abs(view.VerticalOffset - state.Offset) > 1.5;
            double target = Math.Min(state.Target, view.ScrollableHeight); // the content may have shrunk
            if (movedElsewhere || !view.IsLoaded)
            {
                state.Running = false;
                Active.RemoveAt(i);
                continue;
            }

            state.Offset += (target - state.Offset) * share;
            if (Math.Abs(target - state.Offset) < 0.4)
            {
                state.Offset = target;
                state.Running = false;
                Active.RemoveAt(i);
            }
            view.ScrollToVerticalOffset(state.Offset);
        }

        // nothing gliding: stop asking for frames, so an idle notch costs nothing
        if (Active.Count == 0) CompositionTarget.Rendering -= OnFrame;
    }
}
