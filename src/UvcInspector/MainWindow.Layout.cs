using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace UvcInspector;

public partial class MainWindow
{
    private const double MaximumWorkspaceHeight = 2400;
    private bool manualWorkspaceHeight;
    private bool persistLayout;
    private bool layoutUpdateQueued;

    private void InitializeWorkspaceLayout(bool allowPersistence)
    {
        persistLayout = allowPersistence;
        if (persistLayout && Settings.Load().WorkspaceHeight is double saved && double.IsFinite(saved))
        {
            Workspace.Height = Math.Clamp(saved, Workspace.MinHeight, MaximumWorkspaceHeight);
            manualWorkspaceHeight = true;
        }
        MainScroll.SizeChanged += (_, _) => QueueWorkspaceLayout();
        MainContent.LayoutUpdated += (_, _) => QueueWorkspaceLayout();
        Loaded += (_, _) => QueueWorkspaceLayout();
    }

    private void QueueWorkspaceLayout()
    {
        if (manualWorkspaceHeight || layoutUpdateQueued) return;
        layoutUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            layoutUpdateQueued = false;
            if (manualWorkspaceHeight || MainScroll.ActualHeight <= 0) return;
            // StackPanel is measured with infinite height by the ScrollViewer. Allocate the
            // finite viewport space explicitly, leaving room for the cards and bottom details.
            double otherContent = MainContent.Children.Cast<UIElement>().Where(child => child != Workspace).Sum(child => child.DesiredSize.Height)
                + MainContent.Margin.Top + MainContent.Margin.Bottom;
            // Leave one DIP for layout rounding at fractional display scale factors.
            double desired = Math.Max(300, Math.Floor(MainScroll.ActualHeight - otherContent - 1));
            if (Math.Abs(Workspace.Height - desired) >= 1) Workspace.Height = desired;
        }));
    }

    private void ResizeWorkspace(object sender, DragDeltaEventArgs e)
    {
        AdjustWorkspaceHeight(e.VerticalChange);
        e.Handled = true;
    }

    private void AdjustWorkspaceHeight(double delta)
    {
        manualWorkspaceHeight = true;
        Workspace.Height = Math.Clamp(Workspace.Height + delta, Workspace.MinHeight, MaximumWorkspaceHeight);
    }

    private void SaveWorkspaceHeight(object sender, DragCompletedEventArgs e) => PersistWorkspaceLayout();

    private void ResetWorkspaceHeight(object sender, RoutedEventArgs e)
    {
        manualWorkspaceHeight = false;
        QueueWorkspaceLayout();
        PersistWorkspaceLayout();
    }

    private void WorkspaceResizeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Home) ResetWorkspaceHeight(sender, e);
        else if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown)
        {
            AdjustWorkspaceHeight(e.Key switch { Key.Up => -20, Key.Down => 20, Key.PageUp => -100, _ => 100 });
            PersistWorkspaceLayout();
        }
        else return;
        e.Handled = true;
    }

    private void PersistWorkspaceLayout()
    {
        if (!persistLayout) return;
        try { (Settings.Load() with { WorkspaceHeight = manualWorkspaceHeight ? Workspace.Height : null }).Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Unable to save workspace height: {0}", e.Message);
        }
    }

    // Exercise routed UI events and actual WPF layout, without opening a camera or
    // changing the user's preferences. Used by --demo --capture ... --layout-check.
    private async Task<object> CheckWorkspaceLayoutAsync()
    {
        var canvas = (FrameworkElement)Content;
        double originalHeight = canvas.Height;
        async Task SettleAsync()
        {
            await Task.Delay(100);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            UpdateLayout();
        }
        void Drag(double delta)
        {
            WorkspaceResizeThumb.RaiseEvent(new DragDeltaEventArgs(0, delta) { RoutedEvent = Thumb.DragDeltaEvent });
            WorkspaceResizeThumb.RaiseEvent(new DragCompletedEventArgs(0, delta, false) { RoutedEvent = Thumb.DragCompletedEvent });
        }
        void Reset() => AutoHeightButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        canvas.Height = 930; Reset(); await SettleAsync();
        double normal = Workspace.ActualHeight;
        canvas.Height = 1232; await SettleAsync();
        double large = Workspace.ActualHeight, previewBefore = PreviewSurface.ActualHeight;
        var largeLayout = new { CanvasHeight = canvas.ActualHeight, ScrollHeight = MainScroll.ActualHeight, ContentHeight = MainContent.ActualHeight, AssignedHeight = Workspace.Height };
        Drag(180); await SettleAsync();
        double dragged = Workspace.ActualHeight, previewAfter = PreviewSurface.ActualHeight;
        canvas.Height = 1332; await SettleAsync();
        bool manualStable = Math.Abs(Workspace.ActualHeight - dragged) < 1;
        Drag(-10000); await SettleAsync();
        bool minimum = Math.Abs(Workspace.ActualHeight - Workspace.MinHeight) < 1;
        Drag(10000); await SettleAsync();
        bool maximum = Math.Abs(Workspace.ActualHeight - MaximumWorkspaceHeight) < 1;
        canvas.Height = 1232; Reset(); await SettleAsync();
        bool reset = Math.Abs(Workspace.ActualHeight - large) < 1;
        bool healthy = large > normal + 150 && Math.Abs(dragged - large - 180) < 1
            && Math.Abs(previewAfter - previewBefore - 180) < 1 && manualStable && minimum && maximum && reset;
        canvas.Height = originalHeight; Reset(); await SettleAsync();
        return new { Healthy = healthy, LargeLayout = largeLayout, NormalWorkspaceHeight = normal, LargeWorkspaceHeight = large,
            DraggedWorkspaceHeight = dragged, PreviewBefore = previewBefore, PreviewAfter = previewAfter,
            ManualHeightSurvivesViewportResize = manualStable, MinimumRespected = minimum, MaximumRespected = maximum,
            ResetRestoresAutomaticHeight = reset };
    }
}
