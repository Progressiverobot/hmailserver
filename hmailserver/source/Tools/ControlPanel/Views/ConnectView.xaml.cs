using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   public partial class ConnectView : UserControl
   {
      private readonly Action onConnected_;

      public ConnectView(Action onConnected)
      {
         InitializeComponent();
         onConnected_ = onConnected;

         Loaded += (s, e) => PasswordBox.Focus();
         KeyDown += (s, e) =>
         {
            if (e.Key == System.Windows.Input.Key.Enter)
               Connect_Click(this, new RoutedEventArgs());
         };
      }

      private async void Connect_Click(object sender, RoutedEventArgs e)
      {
         string host = HostBox.Text.Trim();
         string user = UserBox.Text.Trim();
         string password = PasswordBox.Password;

         ConnectButton.IsEnabled = false;
         ErrorText.Visibility = Visibility.Collapsed;

         // COM creation must happen on an STA thread; do it here but yield
         // first so the button state renders.
         await Task.Delay(50);

         var session = new ServerSession();
         bool ok = session.Connect(host, user, password, out string error);

         if (ok)
         {
            ServerSession.SetCurrent(session);
            onConnected_();
         }
         else
         {
            ErrorText.Text = error ?? "Connection failed.";
            ErrorText.Visibility = Visibility.Visible;
            ConnectButton.IsEnabled = true;
         }
      }
   }
}
