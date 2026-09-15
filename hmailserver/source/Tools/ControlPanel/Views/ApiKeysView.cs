// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using hMailServer.ControlPanel.Services;

using Typography = hMailServer.ControlPanel.Services.Typography;
using Path = System.Windows.Shapes.Path;
using Card = hMailServer.ControlPanel.Views.Scaffold.Card;
using EmptyState = hMailServer.ControlPanel.Views.Scaffold.EmptyState;
using InlineNotice = hMailServer.ControlPanel.Views.Scaffold.InlineNotice;
using PageHeader = hMailServer.ControlPanel.Views.Scaffold.PageHeader;
using StatusPill = hMailServer.ControlPanel.Views.Scaffold.StatusPill;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;
using static hMailServer.ControlPanel.Services.Loc;

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

      // What the key store as a whole amounts to, at its level. The counts are
      // words, so the level confirms and never carries alone.
      private readonly InlineNotice summary_ = new();
      private readonly TextBlock storePath_ = new();
      private readonly EmptyState empty_ = new()
      {
         Icon = Wpf.Ui.Controls.SymbolRegular.Key24,
         Visibility = Visibility.Collapsed
      };

      /// <summary>The card shown after a key is created, holding the one copy of
      /// the token that will ever exist.</summary>
      private readonly Card newKeyCard_;
      private readonly TextBlock newKeyToken_ = new();
      private readonly TextBlock newKeyLabel_ = new();

      public ApiKeysView()
      {
         var root = new Grid();
         root.SetResourceReference(MarginProperty, "AppPagePadding");
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

         var actions = new StackPanel { Orientation = Orientation.Horizontal };

         var create = new Wpf.Ui.Controls.Button
         {
            Content = L("_Create a key…"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            Margin = new Thickness(0, 0, 8, 0)
         };
         System.Windows.Automation.AutomationProperties.SetName(create, L("Create a new REST API key"));
         System.Windows.Automation.AutomationProperties.SetAutomationId(create, "apikeys-create");
         create.Click += (s, e) => CreateKey_();
         actions.Children.Add(create);

         var reload = new Wpf.Ui.Controls.Button { Content = L("_Reload") };
         System.Windows.Automation.AutomationProperties.SetName(reload, L("Re-read the key store from disk"));
         System.Windows.Automation.AutomationProperties.SetAutomationId(reload, "apikeys-reload");
         reload.Click += (s, e) => Reload_();
         actions.Children.Add(reload);

         root.Children.Add(new PageHeader
         {
            Title = L("REST API keys"),
            Subtitle = L("Credentials for the REST administration API that are not the administrator password: each one expires, can be limited to reading only, to particular domains and to particular source addresses, and can be revoked on its own. The server re-reads the key store on every request, so anything changed here is live at once - no service restart."),
            Actions = actions
         });

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

      /// <summary>One section of the page. Named Section_ and not Card_ because
      /// the component it builds is called Card.</summary>
      private static Card Section_(string cardTitle, string blurb, out StackPanel content)
      {
         content = new StackPanel();
         var card = new Card { Title = cardTitle, Description = blurb, Content = content };
         card.SetResourceReference(MarginProperty, "AppCardGap");
         return card;
      }

      private Card BuildSummaryCard_()
      {
         Card card = Section_(L("Key store"), null, out StackPanel content);

         summary_.Margin = new Thickness(0, 0, 0, 8);
         content.Children.Add(summary_);

         storePath_.TextWrapping = TextWrapping.Wrap;
         storePath_.SetResourceReference(StyleProperty, "TextCaptionTertiary");
         content.Children.Add(storePath_);

         return card;
      }

      /// <summary>
      /// Where a new key is shown. It is the only place the clear text ever
      /// appears, so it says so, in the imperative, before the value itself.
      /// </summary>
      private Card BuildNewKeyCard_()
      {
         Card card = Section_(L("Copy this key now"),
            L("This is the only time it will ever be shown. The store keeps a SHA-256 digest, not the key, so it cannot be recovered or re-displayed - if it is lost, revoke it here and create another."),
            out StackPanel content);

         card.Visibility = Visibility.Collapsed;

         newKeyLabel_.SetResourceReference(StyleProperty, "TextCaption");
         newKeyLabel_.Margin = new Thickness(0, 0, 0, 6);
         content.Children.Add(newKeyLabel_);

         newKeyToken_.FontFamily = new System.Windows.Media.FontFamily(Typography.MonoFontFamily);
         newKeyToken_.FontSize = Typography.Body;
         newKeyToken_.TextWrapping = TextWrapping.Wrap;
         newKeyToken_.Margin = new Thickness(0, 0, 0, 12);
         System.Windows.Automation.AutomationProperties.SetAutomationId(newKeyToken_, "apikeys-new-token");
         System.Windows.Automation.AutomationProperties.SetName(newKeyToken_, L("The new API key, shown once"));
         content.Children.Add(newKeyToken_);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal };

         var copy = new Wpf.Ui.Controls.Button
         {
            Content = L("Copy to clip_board"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            Margin = new Thickness(0, 0, 8, 0)
         };
         System.Windows.Automation.AutomationProperties.SetName(copy, L("Copy the new API key to the clipboard"));
         System.Windows.Automation.AutomationProperties.SetAutomationId(copy, "apikeys-copy");
         copy.Click += (s, e) =>
         {
            try
            {
               Clipboard.SetText(newKeyToken_.Text);
            }
            catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
            {
               // Another process can hold the clipboard open. Saying so is better
               // than a button that silently did nothing with a value that cannot
               // be shown again.
               MessageBox.Show(F("The clipboard could not be written: {0}\r\n\r\nSelect the key above and copy it by hand - it will not be shown again.", ex.Message),
                  L("Control Panel"));
            }
         };
         buttons.Children.Add(copy);

         var done = new Wpf.Ui.Controls.Button { Content = L("_I have copied it") };
         System.Windows.Automation.AutomationProperties.SetName(done, L("Hide the new API key"));
         System.Windows.Automation.AutomationProperties.SetAutomationId(done, "apikeys-dismiss");
         done.Click += (s, e) => HideNewKey_();
         buttons.Children.Add(done);

         content.Children.Add(buttons);
         return card;
      }

      private Card BuildListCard_()
      {
         Card card = Section_(L("API keys"),
            L("Only a digest of each key is stored, so a key cannot be read back from here or from the file. Revoking one removes its section from the store and takes effect on the very next request."),
            out StackPanel content);

         System.Windows.Automation.AutomationProperties.SetAutomationId(list_, "apikeys-list");
         content.Children.Add(list_);
         content.Children.Add(empty_);
         return card;
      }

      private static Card BuildExplanationCard_()
      {
         Card card = Section_(L("How a key is used"), null, out StackPanel content);

         content.Children.Add(Paragraph_(
            L("Send it as a bearer token:  Authorization: Bearer hmapi_...  to the REST listener configured on the API & monitoring page. The administrator password also works and is unrestricted, which is the reason to prefer a key: a key can be read-only, limited to named domains, limited to one source address, given an expiry, and revoked without changing anything else.")));

         content.Children.Add(Paragraph_(
            L("No key of any scope can create or revoke keys - that needs the administrator password, or this page. A key restricted to particular domains is also refused the delivery-queue endpoints outright, because the queue is server-wide and cannot be filtered by domain.")));

         content.Children.Add(Paragraph_(
            L("Every value fails closed. A section whose Scope is anything but the literal \"full\" is read-only, a key whose expiry is missing or unreadable counts as expired, and a section with no usable digest is ignored entirely. That is what makes the store safe to edit by hand: a typo can only ever narrow a key, never widen one.")));

         var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
         links.Children.Add(PageLink_("api", L("REST listener settings…"),
            L("Open API & monitoring, which owns the REST listener's port and TLS settings")));
         content.Children.Add(links);

         return card;
      }

      private static TextBlock Paragraph_(string text)
      {
         var block = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 10) };
         block.SetResourceReference(StyleProperty, "TextBody");
         return block;
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
               L("The key store's location is not known from here. It sits beside hMailServer.INI, which is found through this machine's registry and service table - so keys can only be managed from the server itself, not from a Control Panel connected to another host."));
            storePath_.Text = "";
            return;
         }

         storePath_.Text = L("Store: ") + path;

         List<ApiKeyRecord> keys = ApiKeyStore.Read();

         if (keys.Count == 0)
         {
            // Information and never Normal: an InlineNotice draws its level's
            // severity word, and "Normal" is not a statement about a key store.
            SetSummary_(StatusLevel.Information,
               L("No API keys exist. Every REST request therefore has to carry the administrator password, which carries full authority over every domain and cannot be scoped, expired or revoked on its own."));
         }
         else
         {
            int expired = keys.FindAll(k => k.IsExpired).Count;
            int full = keys.FindAll(k => !k.ReadOnly && !k.IsExpired).Count;
            int unusable = keys.FindAll(k => k.Unusable).Count;

            var parts = new List<string> { Plural_(keys.Count, L("key"), L("keys")) };
            if (full > 0)
               parts.Add(F("{0} with full authority", full));
            if (expired > 0)
               parts.Add(F("{0} expired and now refused", expired));
            if (unusable > 0)
               parts.Add(F("{0} with no usable digest, which the server ignores", unusable));

            SetSummary_(unusable > 0 ? StatusLevel.Warning : StatusLevel.Good, string.Join(", ", parts) + ".");
         }

         foreach (ApiKeyRecord key in keys)
            list_.Children.Add(BuildKeyRow_(key));

         StatusText.Show(empty_, null, keys.Count, null, L("No keys yet."));
      }

      private static string Plural_(int count, string one, string many)
         => count + " " + (count == 1 ? one : many);

      private FrameworkElement BuildKeyRow_(ApiKeyRecord key)
      {
         var border = new Border
         {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12, 10, 12, 10),
            BorderThickness = new Thickness(1)
         };
         border.SetResourceReference(Border.CornerRadiusProperty, "AppControlCornerRadius");
         border.SetResourceReference(Border.BorderBrushProperty, "AppCardBorderBrush");

         var grid = new Grid();
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         var details = new StackPanel();

         var headline = new TextBlock { TextWrapping = TextWrapping.Wrap };
         headline.SetResourceReference(StyleProperty, "TextBody");
         headline.Inlines.Add(new Run(key.Label.Length > 0 ? key.Label : L("(no label)")) { FontWeight = FontWeights.SemiBold });
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
            state = L("Ignored by the server - the stored digest is not a usable SHA-256 value, so this section authenticates nothing. Revoke it and create a replacement.");
         }
         else if (key.IsExpired)
         {
            level = StatusLevel.Information;

            // Two different states, and the unreadable one is not "expired". This
            // page parses the timestamp with one exact format; the server parses it
            // with its own, so a hand-edited value can be readable to the server and
            // not to this page. Saying "the server treats this as expired" would be
            // a claim about the server made from a failure of ours - and the remedy
            // (rewrite the line, or revoke and re-create) is the same either way
            // without needing to assert which of us is right.
            state = key.ExpiresAt == null
               ? L("The stored expiry could not be read in the form this page expects (YYYY-MM-DD HH:MM:SS), so whether the server still accepts this key cannot be told from here. If the line was edited by hand, correct it; otherwise revoke the key and create a replacement.")
               : F("Expired on {0}. Every request it makes is refused.", key.ExpiresAt.Value.ToString("d MMMM yyyy HH:mm", CultureInfo.CurrentCulture)); // no-loc
         }
         else if (!key.ReadOnly)
         {
            level = StatusLevel.Warning;
            state = F("Full authority - this key can change and delete things. Expires {0}.", key.ExpiresAt.Value.ToString("d MMMM yyyy HH:mm", CultureInfo.CurrentCulture)); // no-loc
         }
         else
         {
            level = StatusLevel.Good;
            state = F("Read-only. Expires {0}.", key.ExpiresAt.Value.ToString("d MMMM yyyy HH:mm", CultureInfo.CurrentCulture)); // no-loc
         }

         StatusPresentation presentation = StatusSemantics.For(level);

         var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };

         // The pill carries the colour, the shape and the severity word together;
         // the sentence beside it says what that means for this key.
         stateRow.Children.Add(new StatusPill
         {
            Level = level,
            Text = presentation.SeverityWord,
            Margin = new Thickness(0, 1, 8, 0),
            VerticalAlignment = VerticalAlignment.Top
         });

         var stateText = new TextBlock { Text = state, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
         stateText.SetResourceReference(StyleProperty, "TextCaption");
         System.Windows.Automation.AutomationProperties.SetName(stateText, presentation.SeverityWord + ": " + state);
         stateRow.Children.Add(stateText);

         details.Children.Add(stateRow);

         var restrictions = new List<string>
         {
            key.Domains.Count == 0 ? L("every domain") : F("domains: {0}", string.Join(", ", key.Domains)),
            key.AllowedFrom.Length == 0 ? L("any source address") : F("only from {0}", key.AllowedFrom)
         };

         var scope = new TextBlock
         {
            Text = L("Scope: ") + string.Join("; ", restrictions) + ".",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
         };
         scope.SetResourceReference(StyleProperty, "TextCaptionTertiary");
         details.Children.Add(scope);

         Grid.SetColumn(details, 0);
         grid.Children.Add(details);

         var revoke = new Wpf.Ui.Controls.Button   // per row: no access key, the rows are reached with the arrow keys
         {
            Content = L("Revoke"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Danger,
            VerticalAlignment = VerticalAlignment.Top,
            MinWidth = 84
         };
         System.Windows.Automation.AutomationProperties.SetName(revoke,
            F("Revoke the API key {0}", key.Label.Length > 0 ? key.Label : key.Id));
         System.Windows.Automation.AutomationProperties.SetAutomationId(revoke, "apikeys-revoke-" + key.Id);
         revoke.Click += (s, e) => RevokeKey_(key);
         Grid.SetColumn(revoke, 1);
         grid.Children.Add(revoke);

         border.Child = grid;
         return border;
      }

      private void SetSummary_(StatusLevel level, string text)
      {
         summary_.Level = level;
         summary_.Text = text;
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
            MessageBox.Show(result.Error, L("Control Panel"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
         }

         newKeyLabel_.Text = F("{0}  -  {1}, expires {2}", request.Label, request.Full ? L("full authority") : "read-only",
                             request.Expires.ToString("d MMMM yyyy HH:mm", CultureInfo.CurrentCulture)); // no-loc // no-loc
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
            ? L("It is already expired, so nothing is using it successfully; revoking removes it from this list.")
            : L("Anything using it stops working on its very next request, and the key cannot be restored - only replaced with a new one.");

         if (MessageBox.Show(F("Revoke the API key \"{0}\"?\r\n\r\n{1}", name, consequence),
                L("Control Panel"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
         {
            return;
         }

         if (!ApiKeyStore.Revoke(key.Id, out string error))
         {
            MessageBox.Show(error, L("Control Panel"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
         var dlg = new FluentDialogWindow
         {
            Owner = owner,
            Title = L("Create a REST API key"),
            Width = 560,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
         };
         dlg.SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");

         var panel = new StackPanel { Margin = new Thickness(20) };

         panel.Children.Add(Note_(
            L("The key is shown once, when it is created, and never again. It is live immediately - the server re-reads its key store on every request.")));

         Field_(panel, L("Label"), L("What this key is for, e.g. \"Grafana probe\""), out TextBox labelBox);
         System.Windows.Automation.AutomationProperties.SetAutomationId(labelBox, "apikeys-dialog-label");

         // Read-only first and selected by default, matching the server: a create
         // request that names no scope gets a read-only key, because a key is most
         // often minted for something that only reads and the alternative default
         // hands out a credential that can delete accounts.
         panel.Children.Add(Caption_(L("What it may do")));
         var readOnly = new RadioButton
         {
            Content = L("_Read-only - can list and read, and is refused every request that changes something"),
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 4)
         };
         System.Windows.Automation.AutomationProperties.SetAutomationId(readOnly, "apikeys-dialog-readonly");
         panel.Children.Add(readOnly);

         var full = new RadioButton
         {
            Content = L("_Full authority - can create, change and delete"),
            Margin = new Thickness(0, 0, 0, 12)
         };
         System.Windows.Automation.AutomationProperties.SetAutomationId(full, "apikeys-dialog-full");
         panel.Children.Add(full);

         panel.Children.Add(Caption_(L("Expires")));
         var expires = new DatePicker
         {
            SelectedDate = DateTime.Today.AddDays(ApiKeyStore.DefaultLifetimeDays),
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 200
         };
         System.Windows.Automation.AutomationProperties.SetName(expires, L("Date this key stops working"));
         System.Windows.Automation.AutomationProperties.SetAutomationId(expires, "apikeys-dialog-expires");
         panel.Children.Add(expires);
         panel.Children.Add(Note_(
            F("Defaults to {0} days, which is what the API itself uses when a request does not name one. A key with no expiry is the property that makes the administrator password dangerous, so there is no \"never\" option.", ApiKeyStore.DefaultLifetimeDays)));

         Field_(panel, L("Domains it may act on (optional)"),
            L("example.com, example.net - empty means every domain"), out TextBox domainsBox);
         System.Windows.Automation.AutomationProperties.SetAutomationId(domainsBox, "apikeys-dialog-domains");
         panel.Children.Add(Note_(
            L("A key with a domain list is also refused the delivery-queue endpoints, because the queue is server-wide and cannot be filtered by domain.")));

         Field_(panel, L("Source addresses it may be used from (optional)"),
            "10.0.0.5, or 10.0.0.0/24, or 10.0.0.1-10.0.0.99", out TextBox sourceBox); // no-loc
         System.Windows.Automation.AutomationProperties.SetAutomationId(sourceBox, "apikeys-dialog-source");

         ApiKeyRequest result = null;

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };

         var ok = new Wpf.Ui.Controls.Button
         {
            Content = L("_Create key"),
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
               MessageBox.Show(error, L("Control Panel"));
               labelBox.Focus();
               return;
            }

            if (expires.SelectedDate == null || expires.SelectedDate.Value.Date < DateTime.Today.AddDays(1).Date)
            {
               MessageBox.Show(L("Choose an expiry date at least a day away. A key that expires today would stop working part-way through the day it was made."), L("Control Panel"));
               return;
            }

            ApiKeyStore.NormalizeDomains(domainsBox.Text, out error);
            if (error != null)
            {
               MessageBox.Show(error, L("Control Panel"));
               domainsBox.Focus();
               return;
            }

            string source = sourceBox.Text.Trim();
            if (source.Length > 0 && !ApiKeyStore.LooksLikeSourceRestriction(source))
            {
               MessageBox.Show(L("The source restriction has to be an address (10.0.0.5), a range (10.0.0.1-10.0.0.99) or CIDR (10.0.0.0/24). Leave it empty to accept the key from any address."), L("Control Panel"));
               sourceBox.Focus();
               return;
            }

            result = new ApiKeyRequest
            {
               Label = labelBox.Text.Trim(),
               Full = full.IsChecked is true,
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

         var cancel = new Wpf.Ui.Controls.Button { Content = L("Cancel"), MinWidth = 80, IsCancel = true };
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
            FontSize = Typography.Label,
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
            FontSize = Typography.Body,
            Margin = new Thickness(0, 0, 0, 12)
         };
         System.Windows.Automation.AutomationProperties.SetName(created, caption);
         panel.Children.Add(created);

         box = created;
         return created;
      }
   }
}
