namespace Threadbook.Desktop;

public sealed partial class MainWindow : ILibraryDialogHost
{
    bool ILibraryDialogHost.AllowLibraryDialogAction() => AllowLibraryDialogAction();
}
