using Avalonia.Input;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class BuildServerCredentials : ChromelessWindow
    {
        public BuildServerCredentials()
        {
            InitializeComponent();
        }

        private void OnTextBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OnOK(sender, e);
                e.Handled = true;
            }
        }

        private void OnOK(object _1, RoutedEventArgs _2)
        {
            if (DataContext is Models.BuildServerCredentials credentials)
            {
                credentials.Username = TxtUsername.Text ?? string.Empty;
                credentials.Password = TxtPassword.Text ?? string.Empty;
                Close(true);
            }
            else
            {
                Close(false);
            }
        }

        private void OnCancel(object _1, RoutedEventArgs _2)
        {
            Close(false);
        }
    }
}
