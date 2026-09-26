namespace Kytto.App;

/// <summary>
/// Application entry point.
/// </summary>
/// <remarks>
/// Fully qualified because the project also references Windows Forms — for the
/// tray item (§7.7), which has no WPF equivalent — and both frameworks spell
/// <c>Application</c> the same way.
/// </remarks>
public partial class App : System.Windows.Application
{
}
