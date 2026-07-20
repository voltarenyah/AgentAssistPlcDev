using Agent.Chat;

namespace App;

/// <summary>
/// Sandbox approval prompt (agent sandbox, 2026-07-20): shown by the chat panel when the agent
/// requests a destructive-tier tool. Defaults to Deny (closing/Esc denies).
/// </summary>
public partial class ConfirmToolDialog : System.Windows.Window
{
    public ConfirmToolDialog(ToolConfirmationRequest request)
    {
        InitializeComponent();
        ToolNameText.Text = request.ToolName;
        ArgumentsText.Text = request.ArgumentsSummary;
        BudgetText.Text = $"destructive calls this session: {request.DestructiveCallsSoFar} / {request.SessionBudget}";
    }

    public ToolConfirmation Decision { get; private set; } = ToolConfirmation.Deny;

    private void OnAllowOnce(object sender, System.Windows.RoutedEventArgs e)
    {
        Decision = ToolConfirmation.AllowOnce;
        DialogResult = true;
    }

    private void OnAllowSession(object sender, System.Windows.RoutedEventArgs e)
    {
        Decision = ToolConfirmation.AllowSession;
        DialogResult = true;
    }
}
