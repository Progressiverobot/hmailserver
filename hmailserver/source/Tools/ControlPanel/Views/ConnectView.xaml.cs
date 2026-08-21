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
      private bool connecting_;

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
         // Enter on a field that is already connecting would start a second
         // attempt on top of the first.
         if (connecting_)
            return;

         string host = HostBox.Text.Trim();
         string user = UserBox.Text.Trim();
         string password = PasswordBox.Password;

         SetBusy_(true, "Contacting " + (host.Length == 0 ? "localhost" : host) + "…");

         try
         {
            // Is the machine even there? This runs off the UI thread and answers
            // in seconds, where the DCOM activation below would sit on the UI
            // thread for RPC's own timeout - not redrawing, not moving, and
            // wearing "(Not Responding)" in the title bar. See HostReachability
            // for why the activation itself cannot simply be moved with it.
            HostReachability.Result reach =
               await HostReachability.CheckAsync(host, HostReachability.DefaultTimeout);

            if (!reach.Reachable)
            {
               Fail_(reach.Error);
               return;
            }

            BusyText.Text = "Signing in…";

            // Back on the UI thread deliberately: the COM proxy belongs to the
            // apartment that creates it, and this is the apartment that has to
            // keep using it for the rest of the session. Yield once first so the
            // busy state above actually paints before the blocking calls start.
            await Task.Delay(50);

            var session = new ServerSession();

            if (!session.Connect(host, user, password, out string error))
            {
               Fail_(error ?? "Connection failed.");
               return;
            }

            ServerSession.SetCurrent(session);
            onConnected_();
         }
         catch (Exception ex)
         {
            // Connect reports its own failures through the out parameter, so
            // reaching here means something further out went wrong. Showing it
            // beats an unhandled exception dialog over the sign-in card.
            Fail_(ex.Message);
         }
      }

      private void Fail_(string message)
      {
         ErrorText.Text = message;
         ErrorText.Visibility = Visibility.Visible;
         SetBusy_(false, null);
         PasswordBox.Focus();
      }

      /// <summary>
      /// Swaps the Connect button for a progress ring and locks the fields, so
      /// the credentials cannot be edited underneath an attempt that is using
      /// them. The button is hidden rather than merely disabled: a greyed-out
      /// button is how this screen used to say "working", and it is
      /// indistinguishable from how every screen says "you cannot do that".
      /// </summary>
      private void SetBusy_(bool busy, string message)
      {
         connecting_ = busy;

         if (busy)
         {
            BusyText.Text = message ?? "Connecting…";
            ErrorText.Visibility = Visibility.Collapsed;
         }

         ConnectButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
         BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

         HostBox.IsEnabled = !busy;
         UserBox.IsEnabled = !busy;
         PasswordBox.IsEnabled = !busy;
      }

      private void Totp_Click(object sender, RoutedEventArgs e)
      {
         new TotpSetupDialog(Window.GetWindow(this)).ShowDialog();
      }
   }
}
