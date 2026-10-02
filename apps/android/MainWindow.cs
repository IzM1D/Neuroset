using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Neuroset.App;

public sealed class MainWindow : Window
{
    public MainWindow()
    {
        Title = "Neuroset";
        var frame = new Frame();
        Content = frame;
        var insets = new WindowInsetsController(this, frame);
        Closed += (_, _) => insets.Dispose();
        frame.Navigate(typeof(MainPage));
    }
}
