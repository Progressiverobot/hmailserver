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
            Text = "Jump straight to a task below, or press Ctrl+K to search every page.",
            FontSize = 13,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
         });

         var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
         tiles.Children.Add(Tile(Wpf.Ui.Controls.SymbolRegular.Globe24, "Domains & accounts",
            "Add domains, accounts, aliases and distribution lists.", "domains"));
         tiles.Children.Add(Tile(Wpf.Ui.Controls.SymbolRegular.Server24, "Server settings",
            "Protocols, delivery, anti-spam, anti-virus and advanced options.", "protocols"));
         tiles.Children.Add(Tile(Wpf.Ui.Controls.SymbolRegular.ArrowClockwise24, "Dashboard",
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

      private static Wpf.Ui.Controls.Button Tile(Wpf.Ui.Controls.SymbolRegular icon, string heading, string subtitle, string navKey)
      {
         var icn = new Wpf.Ui.Controls.SymbolIcon { Symbol = icon, FontSize = 24, Margin = new Thickness(0, 0, 0, 10) };
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
            FontSize = 12,
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
            HorizontalContentAlignment = HorizontalAlignment.Left,
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
               (string) ServerSession.Current.Application.Version + " on " + ServerSession.Current.Host + ".";
         }
         catch (Exception)
         {
            serverLine_.Text = "";
         }
      }

      public void OnLeave()
      {
      }
   }
}
