using System;
using System.Net;
using System.Windows;

namespace ClinicManager.Win.Views;

public partial class Shell : ThemedWindow 
{
    public Shell() 
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        
        InitializeComponent();
        
        if(Height > SystemParameters.VirtualScreenHeight || Width > SystemParameters.VirtualScreenWidth)
            WindowState = WindowState.Maximized;
        
    }

        void ShellLoaded(object sender, RoutedEventArgs e) {
            if(Left < 0 || Top < 0)
                WindowState = WindowState.Maximized;
        }
}

