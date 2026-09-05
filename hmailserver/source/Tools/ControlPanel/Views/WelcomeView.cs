// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>Landing page shown after connecting.</summary>
   public class WelcomeView : UserControl, IPageLifecycle
   {
      private readonly TextBlock serverLine_ = new();

      public WelcomeView()
      {
         var panel = new StackPanel { Margin = new Thickness(26, 20, 26, 20), MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };

         var title = new TextBlock { Text = "Welcome" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         panel.Children.Add(title);

         serverLine_.SetResourceReference(StyleProperty, "PageSubtitle");
         panel.Children.Add(serverLine_);

         panel.Children.Add(new TextBlock
         {
            Text = "Start with what you want to do, browse by area below, or press Ctrl+K to search every page and setting.",
            FontSize = Typography.Body,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
         });

         // Keyed on intent, in the administrator's words, and short enough to scan:
         // the twelve reasons this application gets opened, with the one that had no
         // route at all - mail that has stalled - first. The list lives in
         // WelcomeIntents so a test can hold every entry to a page that exists.
         panel.Children.Add(new TextBlock
         {
            Text = "What do you want to do?",
            FontSize = Typography.SectionHeading,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
         });

         var intents = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 10) };
         foreach (WelcomeIntent intent in WelcomeIntents.Entries)
            intents.Children.Add(IntentRow(intent));
         panel.Children.Add(intents);

         panel.Children.Add(new TextBlock
         {
            Text = "Or browse by area",
            FontSize = Typography.SectionHeading,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 8)
         });

         var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
         tiles.Children.Add(Tile(Wpf.Ui.Controls.SymbolRegular.Globe24, "Domains & accounts",
            "Add domains, accounts, aliases and distribution lists.", "domains"));
         tiles.Children.Add(Tile(Wpf.Ui.Controls.SymbolRegular.Server24, "Server settings",
            "Protocols, delivery, anti-spam, anti-virus and advanced options.", "protocols"));
         tiles.Children.Add(Tile(Wpf.Ui.Controls.SymbolRegular.DataUsage24, "Dashboard",
            "Live processed-mail, spam and virus counters with charts.", "dashboard"));
         tiles.Children.Add(Tile(Wpf.Ui.Controls.SymbolRegular.DocumentText24, "Live logs",
            "Stream the server log in real time.", "logs"));
         tiles.Children.Add(Tile(Wpf.Ui.Controls.SymbolRegular.Key24, "Transport security",
            "DANE, MTA-STS, ARC and TLS reporting.", "security"));
         tiles.Children.Add(Tile(Wpf.Ui.Controls.SymbolRegular.ArrowSync24, "Backup & restore",
            "Back up or restore your configuration and data.", "backup"));
         panel.Children.Add(tiles);

         Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
      }

      /// <summary>
      /// One intent as a wide, low button: the task in bold, what the page offers
      /// under it. Left-aligned text, because these are read as a list rather than
      /// glanced at as tiles.
      /// </summary>
      private static Wpf.Ui.Controls.Button IntentRow(WelcomeIntent intent)
      {
         var stack = new StackPanel();
         stack.Children.Add(new TextBlock
         {
            Text = intent.Heading,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 3)
         });
         stack.Children.Add(new TextBlock
         {
            Text = intent.Blurb,
            FontSize = Typography.Caption,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
         });

         var btn = new Wpf.Ui.Controls.Button
         {
            Content = stack,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 12, 10),
            Padding = new Thickness(14, 10, 14, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            Cursor = System.Windows.Input.Cursors.Hand
         };
         System.Windows.Automation.AutomationProperties.SetName(btn, intent.Heading);
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
            Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Center
         };
         icn.Foreground = Services.ThemeTokens.Brand;

         var stack = new StackPanel();
         stack.Children.Add(icn);
         stack.Children.Add(new TextBlock
         {
            Text = heading,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
         });
         stack.Children.Add(new TextBlock
         {
            Text = subtitle,
            FontSize = Typography.Caption,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
         });

         var btn = new Wpf.Ui.Controls.Button
         {
            Content = stack,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(16, 14, 16, 14),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // Stretch rather than Left: the content stack then spans the tile, which
            // is what lets the icon centre on the TILE and leaves the two text
            // blocks left-aligned exactly where they already were (a TextBlock in a
            // stretched vertical StackPanel still draws its text from the left).
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            Height = 128,
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
            serverLine_.Text = "Connected to hMailServer " +
               (string)ServerSession.Current.Application.Version + " on " + ServerSession.Current.Host + ".";
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            serverLine_.Text = "";
         }
      }

      public void OnLeave()
      {
      }
   }
}
