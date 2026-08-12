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
         var panel = new StackPanel { Margin = new Thickness(26, 20, 26, 20), MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left };

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

         // Local helper: a clickable hyperlink that opens in the default browser.
         TextBlock Link(string text, string url, double size = 12.5)
         {
            var tb = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap };
            var hl = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url) };
            hl.RequestNavigate += (s, e) =>
            {
               Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
               e.Handled = true;
            };
            tb.Inlines.Add(hl);
            return tb;
         }

         inner.Children.Add(Link("github.com/Progressiverobot/hmailserver",
            "https://github.com/Progressiverobot/hmailserver"));

         inner.Children.Add(new TextBlock
         {
            Text = "hMailServer is free and open source software, licensed under the GNU AGPLv3. "
                 + "This Control Panel is built with WPF-UI (Fluent design) and LiveCharts2 on .NET 10.",
            FontSize = 11.5,
            Opacity = 0.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0)
         });

         card.Child = inner;
         panel.Children.Add(card);

         // ---- Developer / maintainer card ------------------------------------
         var devCard = new Border { Margin = new Thickness(0, 14, 0, 0) };
         devCard.SetResourceReference(StyleProperty, "Card");
         var dev = new StackPanel();

         dev.Children.Add(new TextBlock
         {
            Text = "Developed & maintained by",
            FontSize = 12,
            Opacity = 0.6,
            Margin = new Thickness(0, 0, 0, 6)
         });
         dev.Children.Add(new TextBlock
         {
            Text = "Christopher Holloway",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
         });
         dev.Children.Add(new TextBlock
         {
            Text = "Progressive Robot Ltd",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.85,
            Margin = new Thickness(0, 0, 0, 10)
         });

         dev.Children.Add(new TextBlock
         {
            Text = "Progressive Robot Ltd is a software engineering company that builds and "
                 + "modernizes production software \u2014 taking mature, real-world systems and "
                 + "bringing them up to current standards of security, reliability and tooling.",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
         });
         dev.Children.Add(new TextBlock
         {
            Text = "The hMailServer 6.x line is one such effort. The original open-source mail "
                 + "server has been rebuilt on a current toolchain (Visual Studio 2026 / MSVC v145 "
                 + "and .NET 10), re-armed with modern cryptography (PBKDF2 / Argon2id, SCRAM-SHA-256, "
                 + "OAuth2) and the transport-security standards expected of a mail server today "
                 + "(DANE + DNSSEC, MTA-STS, ARC, Ed25519 DKIM, TLS-RPT and ACME / Let's Encrypt). "
                 + "It has been hardened against protocol and denial-of-service defects, given "
                 + "Sieve / ManageSieve filtering, health and OpenTelemetry observability, broad "
                 + "MySQL / MariaDB / MS SQL / PostgreSQL support, and this modern Fluent-design "
                 + "Control Panel in place of the legacy administrator.",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
         });

         var web = new TextBlock { FontSize = 13, Margin = new Thickness(0, 0, 0, 2) };
         web.Inlines.Add(new Run("Web  ") { FontWeight = FontWeights.SemiBold });
         var webLink = new Hyperlink(new Run("www.progressiverobot.com"))
         {
            NavigateUri = new Uri("https://www.progressiverobot.com")
         };
         webLink.RequestNavigate += (s, e) =>
         {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
         };
         web.Inlines.Add(webLink);
         dev.Children.Add(web);

         dev.Children.Add(new TextBlock
         {
            Text = "Copyright \u00A9 2026 Christopher Holloway / Progressive Robot Ltd. "
                 + "hMailServer is a trademark of its respective owners; this is an independent, "
                 + "community-maintained fork.",
            FontSize = 11,
            Opacity = 0.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
         });

         devCard.Child = dev;
         panel.Children.Add(devCard);

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
