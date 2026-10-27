using System.Windows;

namespace Ac8SaveEditor;

public partial class AccountDialog : Window
{
    public uint AccountId { get; private set; }

    public AccountDialog()
    {
        InitializeComponent();
        LblHelp.Text = MainWindow.L("The save is encrypted with the Steam account id (the number in Steam\\userdata\\<id>). Type that number.");
        BtnCancel.Content = MainWindow.L("Cancel");
        TxtId.Focus();
    }

    void Ok_Click(object s, RoutedEventArgs e)
    {
        if (uint.TryParse(TxtId.Text.Trim(), out var v) && v > 0) { AccountId = v; DialogResult = true; }
        else MessageBox.Show(MainWindow.L("Enter a positive number."), "AC Zero", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
