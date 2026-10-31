using System.Windows;
using System.Windows.Automation.Peers;

namespace DriveMonitor.App.Accessibility;

/// <summary>Has screen readers read an element out when it appears, such as a new banner.</summary>
/// <remarks>
/// <see cref="System.Windows.Automation.AutomationProperties.LiveSetting"/> marks a live region and says how
/// urgently it is read, but WPF never raises the LiveRegionChanged event that makes Narrator read it.
/// </remarks>
public static class LiveRegion
{
    /// <summary>True: raise LiveRegionChanged each time the element is loaded.</summary>
    public static readonly DependencyProperty AnnounceOnLoadProperty = DependencyProperty.RegisterAttached(
        "AnnounceOnLoad", typeof(bool), typeof(LiveRegion), new PropertyMetadata(false, OnAnnounceOnLoadChanged));

    public static bool GetAnnounceOnLoad(DependencyObject element) => (bool)element.GetValue(AnnounceOnLoadProperty);

    public static void SetAnnounceOnLoad(DependencyObject element, bool value) => element.SetValue(AnnounceOnLoadProperty, value);

    private static void OnAnnounceOnLoadChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement fe)
            return;
        fe.Loaded -= OnLoaded;
        if ((bool)e.NewValue)
            fe.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        // With no screen reader listening, creating the automation peer would be wasted work.
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)
            && UIElementAutomationPeer.CreatePeerForElement((UIElement)sender) is { } peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
