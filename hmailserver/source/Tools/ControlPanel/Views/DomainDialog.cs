using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Modal, tabbed editor for one domain: general, limits, signature and DKIM.
   /// Aliases and distribution lists are managed from the Domains page itself.
   /// </summary>
   public class DomainDialog : Window
   {
      private readonly string domainName_;

      // General
      private readonly CheckBox active_ = new() { Content = "Domain enabled", FontSize = 13 };
      private readonly TextBox postmaster_ = new();

      // Limits
      private readonly TextBox maxSize_ = new();
      private readonly TextBox maxMessageSize_ = new();
      private readonly TextBox maxAccountSize_ = new();
      private readonly CheckBox maxAccountsOn_ = new() { Content = "Limit number of accounts", FontSize = 13 };
      private readonly TextBox maxAccounts_ = new();
      private readonly CheckBox maxAliasesOn_ = new() { Content = "Limit number of aliases", FontSize = 13 };
      private readonly TextBox maxAliases_ = new();
      private readonly CheckBox maxDistsOn_ = new() { Content = "Limit number of distribution lists", FontSize = 13 };
      private readonly TextBox maxDists_ = new();
      private readonly CheckBox plusAddressingOn_ = new() { Content = "Enable plus addressing", FontSize = 13 };
      private readonly TextBox plusChar_ = new();
      private readonly CheckBox greylisting_ = new() { Content = "Enable greylisting for this domain", FontSize = 13 };

      // Signature
      private readonly CheckBox signatureOn_ = new() { Content = "Add signature to outgoing messages", FontSize = 13 };
      private readonly ComboBox signatureMethod_ = new();
      private readonly CheckBox signReplies_ = new() { Content = "Add signature to replies", FontSize = 13 };
      private readonly CheckBox signLocal_ = new() { Content = "Add signature to local e-mail", FontSize = 13 };
      private readonly TextBox signaturePlain_ = NewMemo();
      private readonly TextBox signatureHtml_ = NewMemo();

      // DKIM
      private readonly CheckBox dkimOn_ = new() { Content = "Enable DKIM signing", FontSize = 13 };
      private readonly CheckBox dkimAliases_ = new() { Content = "Sign aliases too", FontSize = 13 };
      private readonly TextBox dkimSelector_ = new();
      private readonly TextBox dkimKeyFile_ = new();
      private readonly ComboBox dkimHeaderCanon_ = new();
      private readonly ComboBox dkimBodyCanon_ = new();
      private readonly ComboBox dkimAlgorithm_ = new();

      public DomainDialog(Window owner, string domainName)
      {
         domainName_ = domainName;

         Owner = owner;
         Title = "Domain - " + domainName;
         Width = 560;
         Height = 640;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

         var root = new Grid { Margin = new Thickness(18) };
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         var header = new TextBlock
         {
            Text = domainName,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 12)
         };
         header.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         Grid.SetRow(header, 0);
         root.Children.Add(header);

         var tabs = new TabControl { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
         tabs.Items.Add(new TabItem { Header = "General", Content = BuildGeneral() });
         tabs.Items.Add(new TabItem { Header = "Limits", Content = BuildLimits() });
         tabs.Items.Add(new TabItem { Header = "Signature", Content = BuildSignature() });
         tabs.Items.Add(new TabItem { Header = "DKIM", Content = BuildDkim() });
         Grid.SetRow(tabs, 1);
         root.Children.Add(tabs);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
         var save = new Wpf.Ui.Controls.Button { Content = "Save", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0) };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel" };
         cancel.Click += (s, e) => Close();
         buttons.Children.Add(save);
         buttons.Children.Add(cancel);
         Grid.SetRow(buttons, 2);
         root.Children.Add(buttons);

         Content = root;
         Loaded += (s, e) => Load();
      }

      private ScrollViewer BuildGeneral()
      {
         var panel = TabPanel();
         panel.Children.Add(active_);
         panel.Children.Add(Label("Postmaster address (mail to unknown recipients is redirected here)"));
         panel.Children.Add(Input(postmaster_));
         return Scroll(panel);
      }

      private ScrollViewer BuildLimits()
      {
         var panel = TabPanel();
         panel.Children.Add(Label("Maximum domain size (MB, 0 = unlimited)"));
         panel.Children.Add(Input(maxSize_));
         panel.Children.Add(Label("Maximum message size (KB, 0 = unlimited)"));
         panel.Children.Add(Input(maxMessageSize_));
         panel.Children.Add(Label("Maximum size for accounts created in this domain (MB, 0 = unlimited)"));
         panel.Children.Add(Input(maxAccountSize_));
         panel.Children.Add(Separator());
         panel.Children.Add(maxAccountsOn_);
         panel.Children.Add(Input(maxAccounts_));
         panel.Children.Add(maxAliasesOn_);
         panel.Children.Add(Input(maxAliases_));
         panel.Children.Add(maxDistsOn_);
         panel.Children.Add(Input(maxDists_));
         panel.Children.Add(Separator());
         panel.Children.Add(plusAddressingOn_);
         panel.Children.Add(Label("Plus addressing character"));
         panel.Children.Add(Input(plusChar_));
         panel.Children.Add(greylisting_);
         return Scroll(panel);
      }

      private ScrollViewer BuildSignature()
      {
         signatureMethod_.Items.Add(Combo("Use only if account has no signature", 1));
         signatureMethod_.Items.Add(Combo("Overwrite account signature", 2));
         signatureMethod_.Items.Add(Combo("Append to account signature", 3));
         StyleCombo(signatureMethod_);

         var panel = TabPanel();
         panel.Children.Add(signatureOn_);
         panel.Children.Add(Label("Signature method"));
         panel.Children.Add(signatureMethod_);
         panel.Children.Add(signReplies_);
         panel.Children.Add(signLocal_);
         panel.Children.Add(Label("Plain-text signature"));
         panel.Children.Add(signaturePlain_);
         panel.Children.Add(Label("HTML signature"));
         panel.Children.Add(signatureHtml_);
         return Scroll(panel);
      }

      private ScrollViewer BuildDkim()
      {
         dkimHeaderCanon_.Items.Add(Combo("Simple", 1));
         dkimHeaderCanon_.Items.Add(Combo("Relaxed", 2));
         StyleCombo(dkimHeaderCanon_);
         dkimBodyCanon_.Items.Add(Combo("Simple", 1));
         dkimBodyCanon_.Items.Add(Combo("Relaxed", 2));
         StyleCombo(dkimBodyCanon_);
         dkimAlgorithm_.Items.Add(Combo("SHA1", 1));
         dkimAlgorithm_.Items.Add(Combo("SHA256", 2));
         StyleCombo(dkimAlgorithm_);

         var panel = TabPanel();
         panel.Children.Add(dkimOn_);
         panel.Children.Add(dkimAliases_);
         panel.Children.Add(Label("Selector"));
         panel.Children.Add(Input(dkimSelector_));
         panel.Children.Add(Label("Private key file"));
         panel.Children.Add(Input(dkimKeyFile_));
         panel.Children.Add(Label("Header canonicalization"));
         panel.Children.Add(dkimHeaderCanon_);
         panel.Children.Add(Label("Body canonicalization"));
         panel.Children.Add(dkimBodyCanon_);
         panel.Children.Add(Label("Signing algorithm"));
         panel.Children.Add(dkimAlgorithm_);
         return Scroll(panel);
      }

      private void Load()
      {
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic d = domains.ItemByName[domainName_];
            active_.IsChecked = (bool) d.Active;
            postmaster_.Text = (string) d.Postmaster ?? "";

            maxSize_.Text = ((int) d.MaxSize).ToString();
            maxMessageSize_.Text = ((int) d.MaxMessageSize).ToString();
            maxAccountSize_.Text = ((int) d.MaxAccountSize).ToString();
            maxAccountsOn_.IsChecked = (bool) d.MaxNumberOfAccountsEnabled;
            maxAccounts_.Text = ((int) d.MaxNumberOfAccounts).ToString();
            maxAliasesOn_.IsChecked = (bool) d.MaxNumberOfAliasesEnabled;
            maxAliases_.Text = ((int) d.MaxNumberOfAliases).ToString();
            maxDistsOn_.IsChecked = (bool) d.MaxNumberOfDistributionListsEnabled;
            maxDists_.Text = ((int) d.MaxNumberOfDistributionLists).ToString();
            plusAddressingOn_.IsChecked = (bool) d.PlusAddressingEnabled;
            plusChar_.Text = (string) d.PlusAddressingCharacter ?? "";
            greylisting_.IsChecked = (bool) d.AntiSpamEnableGreylisting;

            signatureOn_.IsChecked = (bool) d.SignatureEnabled;
            SelectCombo(signatureMethod_, (int) d.SignatureMethod);
            signReplies_.IsChecked = (bool) d.AddSignaturesToReplies;
            signLocal_.IsChecked = (bool) d.AddSignaturesToLocalMail;
            signaturePlain_.Text = (string) d.SignaturePlainText ?? "";
            signatureHtml_.Text = (string) d.SignatureHTML ?? "";

            dkimOn_.IsChecked = (bool) d.DKIMSignEnabled;
            dkimAliases_.IsChecked = (bool) d.DKIMSignAliasesEnabled;
            dkimSelector_.Text = (string) d.DKIMSelector ?? "";
            dkimKeyFile_.Text = (string) d.DKIMPrivateKeyFile ?? "";
            SelectCombo(dkimHeaderCanon_, (int) d.DKIMHeaderCanonicalizationMethod);
            SelectCombo(dkimBodyCanon_, (int) d.DKIMBodyCanonicalizationMethod);
            SelectCombo(dkimAlgorithm_, (int) d.DKIMSigningAlgorithm);

            ServerSession.Release(d);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not load the domain: " + ex.Message, "Control Panel");
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
            dynamic d = domains.ItemByName[domainName_];
            d.Active = active_.IsChecked == true;
            d.Postmaster = postmaster_.Text.Trim();

            if (int.TryParse(maxSize_.Text.Trim(), out int ms)) d.MaxSize = ms;
            if (int.TryParse(maxMessageSize_.Text.Trim(), out int mms)) d.MaxMessageSize = mms;
            if (int.TryParse(maxAccountSize_.Text.Trim(), out int mas)) d.MaxAccountSize = mas;
            d.MaxNumberOfAccountsEnabled = maxAccountsOn_.IsChecked == true;
            if (int.TryParse(maxAccounts_.Text.Trim(), out int mna)) d.MaxNumberOfAccounts = mna;
            d.MaxNumberOfAliasesEnabled = maxAliasesOn_.IsChecked == true;
            if (int.TryParse(maxAliases_.Text.Trim(), out int mnal)) d.MaxNumberOfAliases = mnal;
            d.MaxNumberOfDistributionListsEnabled = maxDistsOn_.IsChecked == true;
            if (int.TryParse(maxDists_.Text.Trim(), out int mnd)) d.MaxNumberOfDistributionLists = mnd;
            d.PlusAddressingEnabled = plusAddressingOn_.IsChecked == true;
            if (plusChar_.Text.Length > 0) d.PlusAddressingCharacter = plusChar_.Text;
            d.AntiSpamEnableGreylisting = greylisting_.IsChecked == true;

            d.SignatureEnabled = signatureOn_.IsChecked == true;
            int sm = ComboValue(signatureMethod_);
            if (sm > 0) d.SignatureMethod = sm;
            d.AddSignaturesToReplies = signReplies_.IsChecked == true;
            d.AddSignaturesToLocalMail = signLocal_.IsChecked == true;
            d.SignaturePlainText = signaturePlain_.Text;
            d.SignatureHTML = signatureHtml_.Text;

            d.DKIMSignEnabled = dkimOn_.IsChecked == true;
            d.DKIMSignAliasesEnabled = dkimAliases_.IsChecked == true;
            d.DKIMSelector = dkimSelector_.Text.Trim();
            d.DKIMPrivateKeyFile = dkimKeyFile_.Text.Trim();
            int hc = ComboValue(dkimHeaderCanon_);
            if (hc > 0) d.DKIMHeaderCanonicalizationMethod = hc;
            int bc = ComboValue(dkimBodyCanon_);
            if (bc > 0) d.DKIMBodyCanonicalizationMethod = bc;
            int alg = ComboValue(dkimAlgorithm_);
            if (alg > 0) d.DKIMSigningAlgorithm = alg;

            d.Save();
            ServerSession.Release(d);
            Close();
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not save the domain: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(domains);
         }
      }

      // ---- UI helpers ----

      private static StackPanel TabPanel() => new() { Margin = new Thickness(4, 12, 4, 4) };

      private static ScrollViewer Scroll(StackPanel panel) => new()
      {
         Content = panel,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
         HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
      };

      private static TextBox NewMemo() => new()
      {
         AcceptsReturn = true,
         Height = 80,
         TextWrapping = TextWrapping.Wrap,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto
      };

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

      private static Border Separator() => new()
      {
         Height = 1,
         Margin = new Thickness(0, 12, 0, 12),
         Background = System.Windows.Media.Brushes.Gray,
         Opacity = 0.3
      };

      private static ComboBoxItem Combo(string text, int value) => new() { Content = text, Tag = value };

      private static void StyleCombo(ComboBox combo)
      {
         combo.FontSize = 13;
         combo.Margin = new Thickness(0, 0, 0, 8);
      }

      private static void SelectCombo(ComboBox combo, int value)
      {
         foreach (ComboBoxItem item in combo.Items)
            if ((int) item.Tag == value)
            {
               combo.SelectedItem = item;
               return;
            }
      }

      private static int ComboValue(ComboBox combo) =>
         combo.SelectedItem is ComboBoxItem item ? (int) item.Tag : 0;
   }
}
