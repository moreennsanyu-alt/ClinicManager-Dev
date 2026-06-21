using System.Windows;

namespace ClinicManager.Win;

public partial class App : Application
{
    static App()
    {
    
    }
    
#if DEBUG
    public bool IsDebug { get { return true; } }
#endif

}

