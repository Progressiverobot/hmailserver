using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Property editor for one distribution list (the membership list is edited
   /// separately via <see cref="RecipientsDialog"/>).
   /// </summary>
   public class DistributionListDialog : Window
   {
      private readonly string domainName_;
      private readonly string address_;

      private readonly CheckBox active_ = new() { Content = "List is active", FontSize = 13 };
      private readonly TextBox addressBox_ = new();
      private readonly ComboBox mode_ = new();
      private readonly CheckBox requireAuth_ = new() { Content = "Require SMTP authentication to send to the list", FontSize = 13 };
      private readonly TextBox requireSender_ = new();

      public DistributionListDialog(Window owner, string domainName, string address)
      {
         domainName_ = domainName;
         address_ = address;
         Owner = owner;
         Title = "Distribution list - " + address;
         Width = 520;
         SizeToContent = SizeToContent.Height;
         ResizeMode = ResizeMode.NoResize;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

         var panel = new StackPanel { Margin = new Thickness(22) };
         var header = new TextBlock { Text = address, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
         header.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         panel.Children.Add(header);

         panel.Children.Add(active_);
         panel.Children.Add(Label("List address"));
         panel.Children.Add(Input(addressBox_));

         mode_.Items.Add(Combo("Public — anyone can send", 0));
         mode_.Items.Add(Combo("Membership — only list members can send", 1));
         mode_.Items.Add(Combo("Announcements only", 2));
         mode_.Items.Add(Combo("Anyone in the domain can send", 3));
         mode_.Items.Add(Combo("Anyone with a server account can send", 4));
         mode_.FontSize = 13;
         mode_.Margin = new Thickness(0, 0, 0, 8);
         panel.Children.Add(Label("Who may send to this list"));
         panel.Children.Add(mode_);

         panel.Children.Add(requireAuth_);
         panel.Children.Add(Label("Require sender address (empty = any)"));
         panel.Children.Add(Input(requireSender_));

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
         var save = new Wpf.Ui.Controls.Button { Content = "Save", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), MinWidth = 80 };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", MinWidth = 80 };
         cancel.Click += (s, e) => Close();
         buttons.Children.Add(save);
         buttons.Children.Add(cancel);
         panel.Children.Add(buttons);

         Content = panel;
         Loaded += (s, e) => Load();
      }

      private dynamic OpenList(dynamic domains)
      {
         dynamic domain = domains.ItemByName[domainName_];
         dynamic lists = domain.DistributionLists;
         dynamic list = lists.ItemByAddress[address_];
         ServerSession.Release(lists);
         ServerSession.Release(domain);
         return list;
      }

      private void Load()
      {
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic l = OpenList(domains);
            active_.IsChecked = (bool) l.Active;
            addressBox_.Text = (string) l.Address ?? "";
            SelectCombo(mode_, (int) l.Mode);
            requireAuth_.IsChecked = (bool) l.RequireSMTPAuth;
            requireSender_.Text = (string) l.RequireSenderAddress ?? "";
            ServerSession.Release(l);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not load the list: " + ex.Message, "Control Panel");
            Close();
         }
         finally
         {
            ServerSession.Release(domains);
         }
      }

      private void Save()
      {
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic l = OpenList(domains);
            l.Active = active_.IsChecked == true;
            if (addressBox_.Text.Trim().Length > 0)
               l.Address = addressBox_.Text.Trim();
            l.Mode = ComboValue(mode_);
            l.RequireSMTPAuth = requireAuth_.IsChecked == true;
            l.RequireSenderAddress = requireSender_.Text.Trim();
            l.Save();
            ServerSession.Release(l);
            Close();
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not save the list: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(domains);
         }
      }

      // ---- UI helpers ----

      private static TextBlock Label(string text)
      {
         var t = new TextBlock { Text = text, FontSize = 12.5, Margin = new Thickness(0, 8, 0, 4) };
         t.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
         return t;
      }

      private static TextBox Input(TextBox box)
      {
         box.FontSize = 13;
         box.Padding = new Thickness(6);
         box.Margin = new Thickness(0, 0, 0, 8);
         box.Background = System.Windows.Media.Brushes.Transparent;
         box.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         return box;
      }

      private static ComboBoxItem Combo(string text, int value) => new() { Content = text, Tag = value };

      private static void SelectCombo(ComboBox combo, int value)
      {
         foreach (ComboBoxItem item in combo.Items)
            if ((int) item.Tag == value) { combo.SelectedItem = item; return; }
      }

      private static int ComboValue(ComboBox combo) => combo.SelectedItem is ComboBoxItem item ? (int) item.Tag : 0;
   }
}
