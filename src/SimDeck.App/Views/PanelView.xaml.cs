using System.Windows.Controls;
using SimDeck.App.ViewModels;

namespace SimDeck.App.Views;

public partial class PanelView : UserControl
{
    public PanelView()
    {
        InitializeComponent();

        // No timer. The gauge pulls fresh values on every frame it draws, so
        // there is no intermediate rate to become the bottleneck.
        Gauge.Sample = () =>
            DataContext is MainViewModel vm ? vm.SamplePanel() : (0, 0, 0, false);
    }
}
