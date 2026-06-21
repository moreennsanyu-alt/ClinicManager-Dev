using System.Windows;
using DevExpress.Xpf.DemoBase;

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

