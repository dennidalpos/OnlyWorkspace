using System.Windows.Forms;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>
/// WinForms shows no tooltip on a disabled control (a locked configuration, an option the profile does not offer),
/// exactly when the user wonders what it is for. A disabled control gets no mouse input: its container does,
/// finds the disabled child under the pointer and shows that child's tooltip itself.
/// </summary>
internal static class DisabledControlTips
{
    public static void Attach(Control root, ToolTip tips)
    {
        Control? shown = null;
        Control? owner = null;

        void Hide()
        {
            if (owner != null && !owner.IsDisposed) tips.Hide(owner);
            shown = owner = null;
        }

        void Hook(Control container)
        {
            if (container is not (Form or Panel or UserControl or SplitContainer or TabControl or TabPage)) return;
            container.MouseMove += (s, e) =>
            {
                var child = container.GetChildAtPoint(e.Location, GetChildAtPointSkip.Invisible);
                var target = child != null && !child.Enabled && !string.IsNullOrEmpty(tips.GetToolTip(child)) ? child : null;
                if (target == shown) return;
                Hide();
                if (target == null) return;
                shown = target;
                owner = container;
                tips.Show(tips.GetToolTip(target) ?? string.Empty, container, e.X + 12, e.Y + 20, tips.AutoPopDelay);
            };
            container.MouseLeave += (s, e) => Hide();
            container.ControlAdded += (s, e) => { if (e.Control != null) Walk(e.Control); };
        }

        void Walk(Control control)
        {
            Hook(control);
            foreach (Control child in control.Controls) Walk(child);
        }

        Walk(root);
    }
}
