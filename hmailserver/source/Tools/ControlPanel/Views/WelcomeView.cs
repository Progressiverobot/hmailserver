// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using hMailServer.ControlPanel.Views.Scaffold;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>Landing page shown after connecting.</summary>
   public class WelcomeView : UserControl, IPageLifecycle
   {
      private readonly PageHeader header_ = new() { Title = L("Welcome") };

      public WelcomeView()
      {
         var panel = new StackPanel { MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };

         // The subtitle says which server this is, once connected (OnEnter).
         panel.Children.Add(header_);

         panel.Children.Add(Text(L("Start with what you want to do, browse by area below, or press Ctrl+K to search every page and setting."),
            "TextSecondary", new Thickness(0, 0, 0, 16)));

         // Keyed on intent, in the administrator's words, and short enough to scan:
         // the twelve reasons this application gets opened, with the one that had no
         // route at all - mail that has stalled - first. The list lives in
         // WelcomeIntents so a test can hold every entry to a page that exists.
         var intents = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
         foreach (WelcomeIntent intent in WelcomeIntents.Entries)
            intents.Children.Add(IntentRow(intent));
         panel.Children.Add(new SettingsSection { Heading = L("What do you want to do?"), Content = intents });

         var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
         tiles.Children.Add(Tile(Wpf.Ui.Controls.SymbolRegular.Globe24, L("Domains & accounts"),
            L("Add domains, accounts, aliases and distribution lists."), "domains"));
         tiles.Children.Add(Tile(Wpf.Ui.Controls.SymbolRegular.Server24, L("Server settings"),
            L("Protocols, delivery, anti-spam, anti-virus and advanced options."), "protocols"));
         tiles.Children.Add(Tile(Wpf.Ui.Controls.SymbolRegular.DataUsage24, L("Dashboard"),
            L("Live processed-mail, spam and virus counters with charts."), "dashboard"));
         tiles.Children.Add(Tile(Wpf.Ui.Controls.SymbolRegular.DocumentText24, L("Live logs"),
            L("Stream the server log in real time."), "logs"));
         tiles.Children.Add(Tile(Wpf.Ui.Controls.SymbolRegular.Key24, L("Transport security"),
            L("DANE, MTA-STS, ARC and TLS reporting."), "security"));
         tiles.Children.Add(Tile(Wpf.Ui.Controls.SymbolRegular.ArrowSync24, L("Backup & restore"),
            L("Back up or restore your configuration and data."), "backup"));
         panel.Children.Add(new SettingsSection { Heading = L("Or browse by area"), Content = tiles, Margin = new Thickness(0) });

         var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
         scroll.SetResourceReference(PaddingProperty, "AppPagePadding");
         Content = scroll;
      }

      private static TextBlock Text(string text, string style, Thickness margin)
      {
         var block = new TextBlock { Text = text, Margin = margin };
         block.SetResourceReference(StyleProperty, style);
         return block;
      }

      /// <summary>
      /// One intent as a wide, low button: the task in bold, what the page offers
      /// under it. Left-aligned text, because these are read as a list rather than
      /// glanced at as tiles.
      /// </summary>
      private static Wpf.Ui.Controls.Button IntentRow(WelcomeIntent intent)
      {
         var stack = new StackPanel();
         stack.Children.Add(Text(L(intent.Heading), "TextBodyStrong", new Thickness(0, 0, 0, 4)));
         stack.Children.Add(Text(L(intent.Blurb), "TextCaption", new Thickness(0)));

         var btn = new Wpf.Ui.Controls.Button
         {
            Content = stack,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(16, 12, 16, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            Cursor = System.Windows.Input.Cursors.Hand
         };
         System.Windows.Automation.AutomationProperties.SetName(btn, L(intent.Heading));
         btn.Click += (s, e) => (Application.Current.MainWindow as MainWindow)?.NavigateTo(intent.Page);
         return btn;
      }

      private static Wpf.Ui.Controls.Button Tile(Wpf.Ui.Controls.SymbolRegular icon, string heading, string subtitle, string navKey)
      {
         // Centred across the WHOLE tile, which needs the stack below to be
         // stretched: with the stack sized to its content, "centre" meant the centre
         // of the widest line of text, so every tile put its icon somewhere
         // different - above the middle of "Domains & accounts" in one and much
         // further left above "Live logs" in another. Six tiles, six positions, none
         // of them chosen. Reported as issue #30.
         var icn = new Wpf.Ui.Controls.SymbolIcon
         {
            Symbol = icon,
            FontSize = 24,
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Center
         };
         // By key, never by a held brush: the brand brush is republished on every
         // theme change, and under High Contrast it is the system highlight.
         icn.SetResourceReference(ForegroundProperty, "AppBrandBrush");

         var stack = new StackPanel();
         stack.Children.Add(icn);
         stack.Children.Add(Text(heading, "TextBodyStrong", new Thickness(0, 0, 0, 4)));
         stack.Children.Add(Text(subtitle, "TextCaption", new Thickness(0)));

         var btn = new Wpf.Ui.Controls.Button
         {
            Content = stack,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(16, 16, 16, 16),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // Stretch rather than Left: the content stack then spans the tile, which
            // is what lets the icon centre on the TILE and leaves the two text
            // blocks left-aligned exactly where they already were (a TextBlock in a
            // stretched vertical StackPanel still draws its text from the left).
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            MinHeight = 128,
            Cursor = System.Windows.Input.Cursors.Hand
         };
         System.Windows.Automation.AutomationProperties.SetName(btn, heading);
         btn.Click += (s, e) => (Application.Current.MainWindow as MainWindow)?.NavigateTo(navKey);
         return btn;
      }

      public void OnEnter()
      {
         try
         {
            header_.Subtitle = F("Connected to hMailServer {0} on {1}.", (string)ServerSession.Current.Application.Version, ServerSession.Current.Host);
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            header_.Subtitle = "";
         }
      }

      public void OnLeave()
      {
      }
   }
}
