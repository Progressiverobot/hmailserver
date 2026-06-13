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
         var panel = new StackPanel { Margin = new Thickness(26, 20, 26, 20), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };

         var title = new TextBlock { Text = "Welcome" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         panel.Children.Add(title);

         serverLine_.SetResourceReference(StyleProperty, "PageSubtitle");
         panel.Children.Add(serverLine_);

         panel.Children.Add(MakeCard("Get started",
            "Use the navigation tree on the left, or press Ctrl+K to jump to any page.\n\n" +
            "\u2022  Domains - add domains, accounts, aliases and distribution lists\n" +
            "\u2022  Settings - protocols, delivery, anti-spam, anti-virus and advanced options\n" +
            "\u2022  Settings > Advanced > Transport security - DANE, MTA-STS, ARC and TLS reporting\n" +
            "\u2022  Utilities - backup, MX query, server sendout and diagnostics"));

         panel.Children.Add(MakeCard("Live monitoring",
            "The Dashboard shows processed mail, spam and virus counters with live throughput " +
            "and session charts. Status > Live logs streams the server log in real time."));

         Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
      }

      private static Border MakeCard(string heading, string body)
      {
         var card = new Border { Margin = new Thickness(0, 0, 0, 12) };
         card.SetResourceReference(StyleProperty, "Card");

         var inner = new StackPanel();
         inner.Children.Add(new TextBlock
         {
            Text = heading,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
         });
         inner.Children.Add(new TextBlock
         {
            Text = body,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            LineHeight = 20
         });
         card.Child = inner;
         return card;
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
