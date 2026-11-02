using System.Windows.Automation.Peers;
using System.Windows.Controls;
namespace NufeCampus.App;

// Navigation lives in the sidebar. Expose the selected content as a pane so
// keyboard/accessibility clients can reach it without invisible tab headers.
public sealed class PageDeck : TabControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new PageDeckPeer(this);
    private sealed class PageDeckPeer(PageDeck owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(PageDeck);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
    }
}
