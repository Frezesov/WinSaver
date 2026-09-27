using System.IO;
using System.Windows;
using WinSaver.ViewModels;

namespace WinSaver;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;

    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
        // The default height shows the whole page, but must not run past a small screen.
        Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 32));
    }

    private void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Папка для скриншотов",
            InitialDirectory = Directory.Exists(_vm.SaveFolder) ? _vm.SaveFolder : null,
        };
        if (dialog.ShowDialog(this) == true)
            _vm.SetFolder(dialog.FolderName);
    }
}
