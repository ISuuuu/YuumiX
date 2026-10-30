using Microsoft.UI.Reactor.Wrappers;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace YuumiX.App.Controls;

public sealed class PixelComboTriggerControl : Button
{
    internal Action? ExpandRequested { get; set; }
    internal Action? CollapseRequested { get; set; }
    internal bool IsExpanded { get; private set; }
    internal int Generation { get; private set; }

    internal void SetExpanded(bool value)
    {
        if (IsExpanded == value) return;
        Generation++;
        var oldValue = IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        IsExpanded = value;
        var newValue = value ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        if (FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer)
        {
            peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, oldValue, newValue);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PixelComboTriggerPeer(this);
}

public sealed class PixelComboTriggerPeer(PixelComboTriggerControl owner) : ButtonAutomationPeer(owner), IExpandCollapseProvider
{
    public ExpandCollapseState ExpandCollapseState => owner.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
    public void Expand()
    {
        if (owner.IsEnabled && !owner.IsExpanded) owner.ExpandRequested?.Invoke();
    }

    public void Collapse()
    {
        if (owner.IsEnabled && owner.IsExpanded) owner.CollapseRequested?.Invoke();
    }
    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPatternCore(patternInterface);
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ComboBox;
}

[GenerateReactorWrapper(typeof(PixelComboTriggerControl), RegisterAssembly = false)]
[WrapContent("Content")]
public partial record PixelComboTriggerControlElement;
