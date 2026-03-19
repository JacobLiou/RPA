using System.Windows;
using System.Windows.Controls;

namespace FlowRunnerGUI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.ScrollToEnd();
        }
    }
}
