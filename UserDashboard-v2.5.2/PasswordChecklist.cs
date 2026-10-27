using System.Drawing;
using System.Windows.Forms;
using UserDashboard.Services;

namespace UserDashboard.Forms;

/// <summary>
/// Reusable password-requirements checklist. As the user types, each rule's
/// icon flips between a red ✘ and a green ✔. Mirrors New-PwdReqRow /
/// Update-PwdChecklist from the PowerShell file.
/// </summary>
internal sealed class PasswordChecklist : UserControl
{
    private readonly Label[] _icons;
    private readonly Label[] _labels;

    public PasswordChecklist()
    {
        BackColor = Color.Transparent;
        AutoSize  = true;

        var rules = PasswordHelper.Rules;
        _icons  = new Label[rules.Count];
        _labels = new Label[rules.Count];

        const int rowHeight = 18;
        for (int i = 0; i < rules.Count; i++)
        {
            _icons[i] = new Label
            {
                Text      = "\u2716", // ✘
                AutoSize  = true,
                Location  = new Point(0, i * rowHeight),
                ForeColor = Color.Firebrick,
                BackColor = Color.Transparent,
                Font      = new Font("Segoe UI Symbol", 8.5f, FontStyle.Regular)
            };
            _labels[i] = new Label
            {
                Text      = rules[i].Label,
                AutoSize  = true,
                Location  = new Point(18, i * rowHeight + 2),
                BackColor = Color.Transparent,
                Font      = new Font("Segoe UI", 8.5f)
            };
            Controls.Add(_icons[i]);
            Controls.Add(_labels[i]);
        }

        // Initial render.
        UpdateFor(string.Empty);
    }

    /// <summary>Re-evaluate every rule against <paramref name="password"/> and update the icons.</summary>
    public void UpdateFor(string? password)
    {
        var pwd = password ?? string.Empty;
        var rules = PasswordHelper.Rules;
        for (int i = 0; i < rules.Count; i++)
        {
            bool ok = rules[i].Test(pwd);
            _icons[i].Text      = ok ? "\u2714" : "\u2716"; // ✔ / ✘
            _icons[i].ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
        }
    }
}
