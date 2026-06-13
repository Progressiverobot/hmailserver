using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>About page: versions, links and component overview.</summary>
   public class AboutView : UserControl, IPageLifecycle
   {
      private readonly TextBlock serverVersion_ = new();

      public AboutView()
      {
         var panel = new StackPanel { Margin = new Thickness(26, 20, 26, 20), MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };

         var title = new TextBlock { Text = "About" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         panel.Children.Add(title);

         var card = new Border { Margin = new Thickness(0, 8, 0, 0) };
         card.SetResourceReference(StyleProperty, "Card");

         var inner = new StackPanel();

         inner.Children.Add(new TextBlock
         {
            Text = "hMailServer Control Panel",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
         });
         inner.Children.Add(new TextBlock
         {
            Text = "Version " + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?")
                 + "  -  .NET " + Environment.Version,
            FontSize = 12.5,
            Opacity = 0.65,
            Margin = new Thickness(0, 0, 0, 12)
         });

         serverVersion_.FontSize = 12.5;
         serverVersion_.Opacity = 0.65;
         serverVersion_.Margin = new Thickness(0, 0, 0, 14);
         inner.Children.Add(serverVersion_);

         inner.Children.Add(new TextBlock
         {
            Text = "A modern administration app for hMailServer: live dashboard, domains "
                 + "and accounts, delivery queue, log streaming, full server settings and "
                 + "the 6.x transport-security features (DANE, MTA-STS, ARC, TLS-RPT, ACME).",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
         });

         var link = new TextBlock { FontSize = 12.5 };
         var hyperlink = new Hyperlink(new Run("github.com/Progressiverobot/hmailserver"))
         {
            NavigateUri = new Uri("https://github.com/Progressiverobot/hmailserver")
         };
         hyperlink.RequestNavigate += (s, e) =>
         {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
         };
         link.Inlines.Add(hyperlink);
         inner.Children.Add(link);

         inner.Children.Add(new TextBlock
         {
            Text = "hMailServer is free and open source software, licensed under the GNU AGPLv3. "
                 + "Built with WPF-UI (Fluent design) and LiveCharts2.",
            FontSize = 11.5,
            Opacity = 0.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0)
         });

         card.Child = inner;
         panel.Children.Add(card);
         Content = panel;
      }

      public void OnEnter()
      {
         try
         {
            serverVersion_.Text = "Connected server: hMailServer " +
               (string) ServerSession.Current.Application.Version +
               " @ " + ServerSession.Current.Host;
         }
         catch (Exception)
         {
            serverVersion_.Text = "";
         }
      }

      public void OnLeave()
      {
      }
   }
}
