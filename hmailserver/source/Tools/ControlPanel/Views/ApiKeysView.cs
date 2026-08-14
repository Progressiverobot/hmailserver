using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using hMailServer.ControlPanel.Services;

using Typography = hMailServer.ControlPanel.Services.Typography;
using Path = System.Windows.Shapes.Path;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// The REST administration API keys: which exist, what each one may do, and
   /// creating and revoking them.
   ///
   /// The keys were previously reachable only over the API they authenticate.
   /// Minting one meant a hand-written POST carrying the administrator password,
   /// against a listener that is off by default - so an administrator who wanted a
   /// scoped, expiring credential for a monitoring probe had, in practice, the
   /// choice between writing curl commands and handing out the administrator
   /// password itself. The second is what happens when the first is inconvenient,
   /// and it is exactly what scoped keys exist to prevent.
   ///
   /// Nothing here reaches the server: the key store is a file on the server's own
   /// machine, and the server re-reads it on every request, so a key created or
   /// revoked on this page is live immediately with no restart. That also means the
   /// page works while the REST listener is switched off, which is the state most
   /// installations are in when they first come here.
   ///
   /// A created key is shown once. There is no "show key" action anywhere on this
   /// page, because the store holds only a SHA-256 digest and offering one would be
   /// promising something the format cannot deliver.
   /// </summary>
   public class ApiKeysView : UserControl, IPageLifecycle
   {
      private readonly StackPanel list_ = new();
      private readonly TextBlock summary_ = new();
      private readonly Path summaryMark_ = new();
      private readonly TextBlock storePath_ = new();

      /// <summary>The card shown after a key is created, holding the one copy of
      /// the token that will ever exist.</summary>
      private readonly Border newKeyCard_;
      private readonly TextBlock newKeyToken_ = new();
      private readonly TextBlock newKeyLabel_ = new();

      public ApiKeysView()
      {
         var root = new Grid { Margin = new Thickness(26, 20, 26, 20) };
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

         var heading = new StackPanel();
         var title = new TextBlock { Text = "REST API keys" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         heading.Children.Add(title);

         var subtitle = new TextBlock
         {
            Text = "Credentials for the REST administration API that are not the administrator password: each one "
                   + "expires, can be limited to reading only, to particular domains and to particular source "
                   + "addresses, and can be revoked on its own. The server re-reads the key store on every request, "
                   + "so anything changed here is live at once - no service restart."
         };
         subtitle.SetResourceReference(StyleProperty, "PageSubtitle");
         heading.Children.Add(subtitle);
         root.Children.Add(heading);

         var cards = new StackPanel { Margin = new Thickness(0, 0, 12, 0), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };

         cards.Children.Add(BuildSummaryCard_());
         newKeyCard_ = BuildNewKeyCard_();
         cards.Children.Add(newKeyCard_);
         cards.Children.Add(BuildListCard_());
         cards.Children.Add(BuildExplanationCard_());

         var scroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
         Grid.SetRow(scroll, 1);
         root.Children.Add(scroll);

         Content = root;
      }

      public void OnEnter() => Reload_();

      public void OnLeave()
      {
         // The token is cleared on the way out rather than being left on a cached
         // page for whoever walks past the console next.
         HideNewKey_();
      }

      // =========================================================================
      // Layout
      // =========================================================================

      private static Border Card_(string cardTitle, string blurb, out StackPanel content)
      {
         var border = new Border { Margin = new Thickness(0, 0, 0, 12) };
         border.SetResourceReference(StyleProperty, "Card");

         var panel = new StackPanel();
         panel.Children.Add(new TextBlock
         {
            Text = cardTitle,
            FontSize = Typography.SectionHeading,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
         });

         if (!string.IsNullOrEmpty(blurb))
         {
            panel.Children.Add(new TextBlock
            {
               Text = blurb,
               FontSize = Typography.Caption,
               TextWrapping = TextWrapping.Wrap,
               Opacity = 0.65,
               Margin = new Thickness(0, 0, 0, 14)
            });
         }

         border.Child = panel;
         content = panel;
         return border;
      }

      private Border BuildSummaryCard_()
      {
         Border card = Card_("Key store", null, out StackPanel content);

         var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };

         summaryMark_.Width = 12;
         summaryMark_.Height = 12;
         summaryMark_.Margin = new Thickness(0, 3, 8, 0);
         summaryMark_.VerticalAlignment = VerticalAlignment.Top;
         row.Children.Add(summaryMark_);

         summary_.FontSize = Typography.Body;
         summary_.TextWrapping = TextWrapping.Wrap;
         summary_.MaxWidth = 700;
         row.Children.Add(summary_);

         content.Children.Add(row);

         storePath_.FontSize = Typography.Caption;
         storePath_.TextWrapping = TextWrapping.Wrap;
         storePath_.Opacity = 0.65;
         content.Children.Add(storePath_);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };

         var create = new Wpf.Ui.Controls.Button
         {
            Content = "Create a key…",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            Margin = new Thickness(0, 0, 8, 0)
         };
         System.Windows.Automation.AutomationProperties.SetName(create, "Create a new REST API key");
         System.Windows.Automation.AutomationProperties.SetAutomationId(create, "apikeys-create");
         create.Click += (s, e) => CreateKey_();
         buttons.Children.Add(create);

         var reload = new Wpf.Ui.Controls.Button { Content = "Reload" };
         System.Windows.Automation.AutomationProperties.SetName(reload, "Re-read the key store from disk");
         System.Windows.Automation.AutomationProperties.SetAutomationId(reload, "apikeys-reload");
         reload.Click += (s, e) => Reload_();
         buttons.Children.Add(reload);

         content.Children.Add(buttons);
         return card;
      }

      /// <summary>
      /// Where a new key is shown. It is the only place the clear text ever
      /// appears, so it says so, in the imperative, before the value itself.
      /// </summary>
      private Border BuildNewKeyCard_()
      {
         Border card = Card_("Copy this key now",
            "This is the only time it will ever be shown. The store keeps a SHA-256 digest, not the key, so it "
            + "cannot be recovered or re-displayed - if it is lost, revoke it here and create another.",
            out StackPanel content);

         card.Visibility = Visibility.Collapsed;

         newKeyLabel_.FontSize = Typography.Caption;
         newKeyLabel_.Opacity = 0.65;
         newKeyLabel_.Margin = new Thickness(0, 0, 0, 6);
         content.Children.Add(newKeyLabel_);

         newKeyToken_.FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New, monospace");
         newKeyToken_.FontSize = 13;
         newKeyToken_.TextWrapping = TextWrapping.Wrap;
         newKeyToken_.Margin = new Thickness(0, 0, 0, 12);
         System.Windows.Automation.AutomationProperties.SetAutomationId(newKeyToken_, "apikeys-new-token");
         System.Windows.Automation.AutomationProperties.SetName(newKeyToken_, "The new API key, shown once");
         content.Children.Add(newKeyToken_);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal };

         var copy = new Wpf.Ui.Controls.Button
         {
            Content = "Copy to clipboard",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            Margin = new Thickness(0, 0, 8, 0)
         };
         System.Windows.Automation.AutomationProperties.SetName(copy, "Copy the new API key to the clipboard");
         System.Windows.Automation.AutomationProperties.SetAutomationId(copy, "apikeys-copy");
         copy.Click += (s, e) =>
         {
            try
            {
               Clipboard.SetText(newKeyToken_.Text);
            }
            catch (Exception ex)
            {
               // Another process can hold the clipboard open. Saying so is better
               // than a button that silently did nothing with a value that cannot
               // be shown again.
               MessageBox.Show("The clipboard could not be written: " + ex.Message
                               + "\r\n\r\nSelect the key above and copy it by hand - it will not be shown again.",
                  "Control Panel");
            }
         };
         buttons.Children.Add(copy);

         var done = new Wpf.Ui.Controls.Button { Content = "I have copied it" };
         System.Windows.Automation.AutomationProperties.SetName(done, "Hide the new API key");
         System.Windows.Automation.AutomationProperties.SetAutomationId(done, "apikeys-dismiss");
         done.Click += (s, e) => HideNewKey_();
         buttons.Children.Add(done);

         content.Children.Add(buttons);
         return card;
      }

      private Border BuildListCard_()
      {
         Border card = Card_("Keys",
            "Only a digest of each key is stored, so a key cannot be read back from here or from the file. "
            + "Revoking one removes its section from the store and takes effect on the very next request.",
            out StackPanel content);

         System.Windows.Automation.AutomationProperties.SetAutomationId(list_, "apikeys-list");
         content.Children.Add(list_);
         return card;
      }

      private static Border BuildExplanationCard_()
      {
         Border card = Card_("How a key is used", null, out StackPanel content);

         content.Children.Add(Paragraph_(
            "Send it as a bearer token:  Authorization: Bearer hmapi_...  to the REST listener configured on the "
            + "API & monitoring page. The administrator password also works and is unrestricted, which is the "
            + "reason to prefer a key: a key can be read-only, limited to named domains, limited to one source "
            + "address, given an expiry, and revoked without changing anything else."));

         content.Children.Add(Paragraph_(
            "No key of any scope can create or revoke keys - that needs the administrator password, or this page. "
            + "A key restricted to particular domains is also refused the delivery-queue endpoints outright, "
            + "because the queue is server-wide and cannot be filtered by domain."));

         content.Children.Add(Paragraph_(
            "Every value fails closed. A section whose Scope is anything but the literal \"full\" is read-only, a "
            + "key whose expiry is missing or unreadable counts as expired, and a section with no usable digest is "
            + "ignored entirely. That is what makes the store safe to edit by hand: a typo can only ever narrow a "
            + "key, never widen one."));

         var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
         links.Children.Add(PageLink_("api", "REST listener settings…",
            "Open API & monitoring, which owns the REST listener's port and TLS settings"));
         content.Children.Add(links);

         return card;
      }

      private static TextBlock Paragraph_(string text)
      {
         return new TextBlock
         {
            Text = text,
            FontSize = Typography.Body,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
         };
      }

      /// <summary>Same construction as SpamOverviewView.PageLink: the way out to the
      /// page that owns the thing being described.</summary>
      private static FrameworkElement PageLink_(string page, string caption, string accessibleName)
      {
         var button = new Wpf.Ui.Controls.Button
         {
            Content = caption,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            FontSize = Typography.Caption,
            Padding = new Thickness(8, 3, 8, 3),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = accessibleName
         };
         System.Windows.Automation.AutomationProperties.SetName(button, accessibleName);
         System.Windows.Automation.AutomationProperties.SetAutomationId(button, "apikeys-open-" + page);
         button.Click += (s, e) => (Application.Current?.MainWindow as MainWindow)?.NavigateTo(page);
         return button;
      }

      // =========================================================================
      // Data
      // =========================================================================

      private void Reload_()
      {
         list_.Children.Clear();

         string path = ApiKeyStore.StoreFile;

         if (path == null)
         {
            SetSummary_(StatusLevel.Information,
               "The key store's location is not known from here. It sits beside hMailServer.INI, which is found "
               + "through this machine's registry and service table - so keys can only be managed from the server "
               + "itself, not from a Control Panel connected to another host.");
            storePath_.Text = "";
            return;
         }

         storePath_.Text = "Store: " + path;

         List<ApiKeyRecord> keys = ApiKeyStore.Read();

         if (keys.Count == 0)
         {
            SetSummary_(StatusLevel.Normal,
               "No API keys exist. Every REST request therefore has to carry the administrator password, which "
               + "carries full authority over every domain and cannot be scoped, expired or revoked on its own.");
         }
         else
         {
            int expired = keys.FindAll(k => k.IsExpired).Count;
            int full = keys.FindAll(k => !k.ReadOnly && !k.IsExpired).Count;
            int unusable = keys.FindAll(k => k.Unusable).Count;

            var parts = new List<string> { Plural_(keys.Count, "key", "keys") };
            if (full > 0)
               parts.Add(full + " with full authority");
            if (expired > 0)
               parts.Add(expired + " expired and now refused");
            if (unusable > 0)
               parts.Add(unusable + " with no usable digest, which the server ignores");

            SetSummary_(unusable > 0 ? StatusLevel.Warning : StatusLevel.Good, string.Join(", ", parts) + ".");
         }

         foreach (ApiKeyRecord key in keys)
            list_.Children.Add(BuildKeyRow_(key));

         if (keys.Count == 0)
         {
            list_.Children.Add(new TextBlock
            {
               Text = "No keys yet.",
               FontSize = Typography.Body,
               Opacity = 0.65
            });
         }
      }

      private static string Plural_(int count, string one, string many)
         => count + " " + (count == 1 ? one : many);

      private FrameworkElement BuildKeyRow_(ApiKeyRecord key)
      {
         var border = new Border
         {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12, 10, 12, 10),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
         };
         border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

         var grid = new Grid();
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         var details = new StackPanel();

         var headline = new TextBlock { FontSize = Typography.Body, TextWrapping = TextWrapping.Wrap };
         headline.Inlines.Add(new Run(key.Label.Length > 0 ? key.Label : "(no label)") { FontWeight = FontWeights.SemiBold });
         // A Run is an Inline and has no Opacity; the secondary text brush is how
         // the rest of the application recedes a caption, and it is theme-aware,
         // which a hard-coded opacity would not be.
         var idRun = new Run("   " + key.Id) { FontSize = Typography.Caption };
         idRun.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
         headline.Inlines.Add(idRun);
         details.Children.Add(headline);

         // The status line carries a word as well as a shape and a colour, so the
         // difference between a live full-authority key and an expired read-only
         // one survives greyscale and colour blindness.
         StatusLevel level;
         string state;

         if (key.Unusable)
         {
            level = StatusLevel.Warning;
            state = "Ignored by the server - the stored digest is not a usable SHA-256 value, so this section "
                    + "authenticates nothing. Revoke it and create a replacement.";
         }
         else if (key.IsExpired)
         {
            level = StatusLevel.Information;
            state = key.ExpiresAt == null
               ? "Expired - the stored expiry cannot be read, which the server treats as expired."
               : "Expired on " + key.ExpiresAt.Value.ToString("d MMMM yyyy HH:mm", CultureInfo.CurrentCulture)
                 + ". Every request it makes is refused.";
         }
         else if (!key.ReadOnly)
         {
            level = StatusLevel.Warning;
            state = "Full authority - this key can change and delete things. Expires "
                    + key.ExpiresAt.Value.ToString("d MMMM yyyy HH:mm", CultureInfo.CurrentCulture) + ".";
         }
         else
         {
            level = StatusLevel.Good;
            state = "Read-only. Expires "
                    + key.ExpiresAt.Value.ToString("d MMMM yyyy HH:mm", CultureInfo.CurrentCulture) + ".";
         }

         StatusPresentation presentation = StatusSemantics.For(level);

         var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };

         var mark = new Path
         {
            Width = 10,
            Height = 10,
            Margin = new Thickness(0, 4, 8, 0),
            VerticalAlignment = VerticalAlignment.Top
         };
         ShapeMarkVisuals.ApplyMark(mark, presentation.Shape, presentation.BrushKey);
         stateRow.Children.Add(mark);

         var stateText = new TextBlock { FontSize = Typography.Caption, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
         stateText.Inlines.Add(new Run(presentation.SeverityWord + ": ") { FontWeight = FontWeights.SemiBold });
         stateText.Inlines.Add(new Run(state));
         System.Windows.Automation.AutomationProperties.SetName(stateText, presentation.SeverityWord + ": " + state);
         stateRow.Children.Add(stateText);

         details.Children.Add(stateRow);

         var restrictions = new List<string>
         {
            key.Domains.Count == 0 ? "every domain" : "domains: " + string.Join(", ", key.Domains),
            key.AllowedFrom.Length == 0 ? "any source address" : "only from " + key.AllowedFrom
         };

         details.Children.Add(new TextBlock
         {
            Text = "Scope: " + string.Join("; ", restrictions) + ".",
            FontSize = Typography.Caption,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18, 4, 0, 0)
         });

         Grid.SetColumn(details, 0);
         grid.Children.Add(details);

         var revoke = new Wpf.Ui.Controls.Button
         {
            Content = "Revoke",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Danger,
            VerticalAlignment = VerticalAlignment.Top,
            MinWidth = 84
         };
         System.Windows.Automation.AutomationProperties.SetName(revoke,
            "Revoke the API key " + (key.Label.Length > 0 ? key.Label : key.Id));
         System.Windows.Automation.AutomationProperties.SetAutomationId(revoke, "apikeys-revoke-" + key.Id);
         revoke.Click += (s, e) => RevokeKey_(key);
         Grid.SetColumn(revoke, 1);
         grid.Children.Add(revoke);

         border.Child = grid;
         return border;
      }

      private void SetSummary_(StatusLevel level, string text)
      {
         StatusPresentation presentation = StatusSemantics.For(level);
         ShapeMarkVisuals.ApplyMark(summaryMark_, presentation.Shape, presentation.BrushKey);

         summary_.Inlines.Clear();
         summary_.Inlines.Add(new Run(presentation.SeverityWord + ": ") { FontWeight = FontWeights.SemiBold });
         summary_.Inlines.Add(new Run(text));

         System.Windows.Automation.AutomationProperties.SetName(summary_, presentation.SeverityWord + ": " + text);
      }

      // =========================================================================
      // Actions
      // =========================================================================

      private void CreateKey_()
      {
         var request = ApiKeyDialog.Ask(Window.GetWindow(this));
         if (request == null)
            return;

         ApiKeyStore.CreateResult result = ApiKeyStore.Create(
            request.Label, request.Full, request.Expires, request.AllowedFrom, request.Domains);

         if (!result.Succeeded)
         {
            MessageBox.Show(result.Error, "Control Panel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
         }

         newKeyLabel_.Text = request.Label + "  -  " + (request.Full ? "full authority" : "read-only")
                             + ", expires " + request.Expires.ToString("d MMMM yyyy HH:mm", CultureInfo.CurrentCulture);
         newKeyToken_.Text = result.Token;
         newKeyCard_.Visibility = Visibility.Visible;
         newKeyCard_.BringIntoView();

         Reload_();
      }

      private void HideNewKey_()
      {
         newKeyToken_.Text = "";
         newKeyLabel_.Text = "";
         newKeyCard_.Visibility = Visibility.Collapsed;
      }

      private void RevokeKey_(ApiKeyRecord key)
      {
         string name = key.Label.Length > 0 ? key.Label : key.Id;

         string consequence = key.IsExpired
            ? "It is already expired, so nothing is using it successfully; revoking removes it from this list."
            : "Anything using it stops working on its very next request, and the key cannot be restored - only "
              + "replaced with a new one.";

         if (MessageBox.Show("Revoke the API key \"" + name + "\"?\r\n\r\n" + consequence,
                "Control Panel", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
         {
            return;
         }

         if (!ApiKeyStore.Revoke(key.Id, out string error))
         {
            MessageBox.Show(error, "Control Panel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
         }

         Reload_();
      }
   }

   /// <summary>
   /// What to create, asked for before anything is written. A dialog rather than
   /// inline fields because the answers are only meaningful together: a key's
   /// scope, expiry and restrictions are one decision, and the token that comes
   /// back cannot be changed afterwards.
   /// </summary>
   internal sealed class ApiKeyRequest
   {
      public string Label;
      public bool Full;
      public DateTime Expires;
      public string AllowedFrom;
      public string Domains;
   }

   internal static class ApiKeyDialog
   {
      public static ApiKeyRequest Ask(Window owner)
      {
         var dlg = new Window
         {
            Owner = owner,
            Title = "Create a REST API key",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
         };
         dlg.SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");

         var panel = new StackPanel { Margin = new Thickness(20) };

         panel.Children.Add(Note_(
            "The key is shown once, when it is created, and never again. It is live immediately - the server "
            + "re-reads its key store on every request."));

         var label = Field_(panel, "Label", "What this key is for, e.g. \"Grafana probe\"", out TextBox labelBox);
         System.Windows.Automation.AutomationProperties.SetAutomationId(labelBox, "apikeys-dialog-label");

         // Read-only first and selected by default, matching the server: a create
         // request that names no scope gets a read-only key, because a key is most
         // often minted for something that only reads and the alternative default
         // hands out a credential that can delete accounts.
         panel.Children.Add(Caption_("What it may do"));
         var readOnly = new RadioButton
         {
            Content = "Read-only - can list and read, and is refused every request that changes something",
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 4)
         };
         System.Windows.Automation.AutomationProperties.SetAutomationId(readOnly, "apikeys-dialog-readonly");
         panel.Children.Add(readOnly);

         var full = new RadioButton
         {
            Content = "Full authority - can create, change and delete",
            Margin = new Thickness(0, 0, 0, 12)
         };
         System.Windows.Automation.AutomationProperties.SetAutomationId(full, "apikeys-dialog-full");
         panel.Children.Add(full);

         panel.Children.Add(Caption_("Expires"));
         var expires = new DatePicker
         {
            SelectedDate = DateTime.Today.AddDays(ApiKeyStore.DefaultLifetimeDays),
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 200
         };
         System.Windows.Automation.AutomationProperties.SetName(expires, "Date this key stops working");
         System.Windows.Automation.AutomationProperties.SetAutomationId(expires, "apikeys-dialog-expires");
         panel.Children.Add(expires);
         panel.Children.Add(Note_(
            "Defaults to " + ApiKeyStore.DefaultLifetimeDays + " days, which is what the API itself uses when a "
            + "request does not name one. A key with no expiry is the property that makes the administrator "
            + "password dangerous, so there is no \"never\" option."));

         Field_(panel, "Domains it may act on (optional)",
            "example.com, example.net - empty means every domain", out TextBox domainsBox);
         System.Windows.Automation.AutomationProperties.SetAutomationId(domainsBox, "apikeys-dialog-domains");
         panel.Children.Add(Note_(
            "A key with a domain list is also refused the delivery-queue endpoints, because the queue is "
            + "server-wide and cannot be filtered by domain."));

         Field_(panel, "Source addresses it may be used from (optional)",
            "10.0.0.5, or 10.0.0.0/24, or 10.0.0.1-10.0.0.99", out TextBox sourceBox);
         System.Windows.Automation.AutomationProperties.SetAutomationId(sourceBox, "apikeys-dialog-source");

         ApiKeyRequest result = null;

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };

         var ok = new Wpf.Ui.Controls.Button
         {
            Content = "Create key",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 100,
            IsDefault = true
         };
         ok.Click += (s, e) =>
         {
            // Validated here as well as in the store, so a rejected value is a
            // message beside the box that produced it rather than after the dialog
            // has closed and taken the other answers with it.
            string error = ApiKeyStore.ValidateLabel(labelBox.Text);
            if (error != null)
            {
               MessageBox.Show(error, "Control Panel");
               labelBox.Focus();
               return;
            }

            if (expires.SelectedDate == null || expires.SelectedDate.Value.Date < DateTime.Today.AddDays(1).Date)
            {
               MessageBox.Show("Choose an expiry date at least a day away. A key that expires today would stop "
                               + "working part-way through the day it was made.", "Control Panel");
               return;
            }

            ApiKeyStore.NormalizeDomains(domainsBox.Text, out error);
            if (error != null)
            {
               MessageBox.Show(error, "Control Panel");
               domainsBox.Focus();
               return;
            }

            string source = sourceBox.Text.Trim();
            if (source.Length > 0 && !ApiKeyStore.LooksLikeSourceRestriction(source))
            {
               MessageBox.Show("The source restriction has to be an address (10.0.0.5), a range "
                               + "(10.0.0.1-10.0.0.99) or CIDR (10.0.0.0/24). Leave it empty to accept the key "
                               + "from any address.", "Control Panel");
               sourceBox.Focus();
               return;
            }

            result = new ApiKeyRequest
            {
               Label = labelBox.Text.Trim(),
               Full = full.IsChecked == true,
               // End of the chosen day rather than midnight at its start: a key
               // asked to last until the 30th that stopped working on the 29th
               // would be a day short of what was chosen.
               Expires = expires.SelectedDate.Value.Date.AddDays(1).AddSeconds(-1),
               AllowedFrom = source,
               Domains = domainsBox.Text.Trim()
            };

            dlg.DialogResult = true;
            dlg.Close();
         };
         buttons.Children.Add(ok);

         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
         cancel.Click += (s, e) => dlg.Close();
         buttons.Children.Add(cancel);

         panel.Children.Add(buttons);
         dlg.Content = new ScrollViewer { Content = panel, MaxHeight = 640, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

         labelBox.Loaded += (s, e) => labelBox.Focus();

         return dlg.ShowDialog() == true ? result : null;
      }

      private static TextBlock Caption_(string text)
      {
         return new TextBlock
         {
            Text = text,
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 6)
         };
      }

      private static TextBlock Note_(string text)
      {
         return new TextBlock
         {
            Text = text,
            FontSize = Typography.Caption,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.65,
            Margin = new Thickness(0, 0, 0, 12)
         };
      }

      private static FrameworkElement Field_(Panel panel, string caption, string placeholder, out TextBox box)
      {
         panel.Children.Add(Caption_(caption));

         var created = new Wpf.Ui.Controls.TextBox
         {
            PlaceholderText = placeholder,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 12)
         };
         System.Windows.Automation.AutomationProperties.SetName(created, caption);
         panel.Children.Add(created);

         box = created;
         return created;
      }
   }
}
