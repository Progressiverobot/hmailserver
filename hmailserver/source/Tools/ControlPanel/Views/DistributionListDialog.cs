using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Property editor for one distribution list (the membership list is edited
   /// separately via <see cref="RecipientsDialog"/>).
   /// </summary>
   public class DistributionListDialog : FluentDialogWindow
   {
      private readonly string domainName_;
      private readonly string address_;

      private readonly CheckBox active_ = new() { Content = "List is active", FontSize = Typography.Body };
      private readonly TextBox addressBox_ = new();
      private readonly ComboBox mode_ = new();
      private readonly CheckBox requireAuth_ = new() { Content = "Require SMTP authentication to send to the list", FontSize = Typography.Body };
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
         var header = new TextBlock { Text = address, FontSize = Typography.DialogTitle, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
         header.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         panel.Children.Add(header);

         panel.Children.Add(active_);
         panel.Children.Add(Label("List address", addressBox_));
         panel.Children.Add(Input(addressBox_));

         // Four modes, not five. There used to be a fifth - "Anyone with a server
         // account can send", mode 4 - and it was the most dangerous entry in this
         // dialog, because it did the opposite of what it said.
         //
         // The server does not implement it: DistributionList::ListMode stops at
         // LMDomainMembers = 3 and RecipientParser::UserCanSendToList_ has no
         // branch for a fifth mode. Mode 4 existed only as an enumerator in the
         // type library. put_Mode's switch had no case for it and seeded its local
         // with LMPublic, so choosing it stored "anyone may send" - and get_Mode's
         // default reported it back as "Public", so the only symptom was a
         // selection that looked as though it had not stuck.
         //
         // An administrator picking the more restrictive-sounding of the two
         // "anyone..." entries, to keep outsiders off a list, was silently given
         // the single most permissive setting the server has. put_Mode now refuses
         // the value outright; the option is gone from here so nobody can reach it.
         mode_.Items.Add(Combo("Public — anyone can send", 0));
         mode_.Items.Add(Combo("Membership — only list members can send", 1));
         mode_.Items.Add(Combo("Announcements only", 2));
         mode_.Items.Add(Combo("Anyone in the domain can send", 3));
         mode_.FontSize = Typography.Body;
         mode_.Margin = new Thickness(0, 0, 0, 8);
         panel.Children.Add(Label("Who may send to this list", mode_));
         panel.Children.Add(mode_);
         panel.Children.Add(new TextBlock
         {
            Text = "\"Anyone in the domain\" means the sender's address is at a domain this server hosts, which an "
                   + "outsider can claim unless the list also requires authentication. Tick that below if the list "
                   + "must be restricted to people who have logged in.",
            FontSize = Services.Typography.Caption,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.65,
            Margin = new Thickness(0, 0, 0, 10)
         });

         panel.Children.Add(requireAuth_);
         panel.Children.Add(Label("Require sender address (empty = any)", requireSender_));
         panel.Children.Add(Input(requireSender_));

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
         // Enter saves, Escape cancels. Neither worked before.
         var save = new Wpf.Ui.Controls.Button { Content = "Save", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), MinWidth = 80, IsDefault = true };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
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

      /// <summary>
      /// A caption, and - when the editor it captions is passed in - that editor's
      /// accessible name. A TextBlock above a control tells UI Automation nothing.
      /// The two checkboxes need nothing: a content control names itself.
      /// </summary>
      private static TextBlock Label(string text, FrameworkElement editor = null)
      {
         var t = new TextBlock { Text = text, FontSize = Typography.Label, Margin = new Thickness(0, 8, 0, 4) };
         t.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");

         if (editor != null)
            AutomationProperties.SetName(editor, AccessibleNames.Qualify(text, ""));

         return t;
      }

      private static TextBox Input(TextBox box)
      {
         box.FontSize = Typography.Body;
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
