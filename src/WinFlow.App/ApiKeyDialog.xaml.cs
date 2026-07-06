using System.Windows;
using WinFlow.Core.Abstractions;

namespace WinFlow.App;

public partial class ApiKeyDialog : Window
{
    private readonly ICredentialStore _credentials;

    public ApiKeyDialog(ICredentialStore credentials)
    {
        InitializeComponent();
        _credentials = credentials;

        bool hasKey = !string.IsNullOrEmpty(_credentials.GetApiKey());
        StatusLabel.Text = hasKey
            ? "A key is saved. Enter a new one to replace it."
            : "No key saved yet. Paste your OpenAI API key (sk-…):";
        RemoveButton.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string key = KeyBox.Password.Trim();
        if (key.Length == 0)
        {
            MessageBox.Show(this, "Enter a key, or press Cancel.", "WinFlow");
            return;
        }

        _credentials.SetApiKey(key);
        DialogResult = true;
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        _credentials.DeleteApiKey();
        DialogResult = true;
    }
}
