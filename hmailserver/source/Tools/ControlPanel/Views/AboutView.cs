// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using hMailServer.ControlPanel.Services;
using hMailServer.ControlPanel.Views.Scaffold;
using Typography = hMailServer.ControlPanel.Services.Typography;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>About page: versions, links and component overview.</summary>
   public class AboutView : UserControl, IPageLifecycle
   {
      private readonly TextBlock serverVersion_ = new();

      public AboutView()
      {
         var panel = new StackPanel { MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left };

         panel.Children.Add(new PageHeader { Title = L("About") });

         // ---- the program ------------------------------------------------------
         var inner = new StackPanel();

         serverVersion_.SetResourceReference(StyleProperty, "TextCaption");
         serverVersion_.Margin = new Thickness(0, 0, 0, 12);
         inner.Children.Add(serverVersion_);

         inner.Children.Add(Text(L("A modern administration app for hMailServer: live dashboard, domains and accounts, delivery queue, log streaming, full server settings and the 6.x transport-security features (DANE, MTA-STS, ARC, TLS-RPT, ACME)."),
            "TextBody", new Thickness(0, 0, 0, 12)));

         inner.Children.Add(Link("github.com/Progressiverobot/hmailserver", "https://github.com/Progressiverobot/hmailserver"));

         var card = new Card
         {
            Title = L("hMailServer Control Panel"),
            Description = F("Version {0}  -  .NET {1}", System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?", Environment.Version),
            Content = inner,
            Footer = Text(L("hMailServer is free and open source software, licensed under the GNU AGPLv3. This Control Panel is built with WPF-UI (Fluent design) and LiveCharts2 on .NET 10."),
               "TextCaptionTertiary", new Thickness(0))
         };
         card.SetResourceReference(MarginProperty, "AppCardGap");
         panel.Children.Add(card);

         // ---- Developer / maintainer card ------------------------------------
         var dev = new StackPanel();

         dev.Children.Add(Text("Christopher Holloway", "TextSubtitle", new Thickness(0, 0, 0, 2))); // no-loc
         dev.Children.Add(Text("Progressive Robot Ltd", "TextBodyStrong", new Thickness(0, 0, 0, 12))); // no-loc

         dev.Children.Add(Text(L("Progressive Robot Ltd is a software engineering company that builds and modernizes production software — taking mature, real-world systems and bringing them up to current standards of security, reliability and tooling."),
            "TextBody", new Thickness(0, 0, 0, 12)));
         dev.Children.Add(Text(L("The hMailServer 6.x line is one such effort. The original open-source mail server has been rebuilt on a current toolchain (Visual Studio 2026 / MSVC v145 and .NET 10), re-armed with modern cryptography (PBKDF2 / Argon2id, SCRAM-SHA-256, OAuth2) and the transport-security standards expected of a mail server today (DANE + DNSSEC, MTA-STS, ARC, Ed25519 DKIM, TLS-RPT and ACME / Let's Encrypt). It has been hardened against protocol and denial-of-service defects, given Sieve / ManageSieve filtering, health and OpenTelemetry observability, broad MySQL / MariaDB / MS SQL / PostgreSQL support, and this modern Fluent-design Control Panel in place of the legacy administrator."),
            "TextBody", new Thickness(0, 0, 0, 12)));

         var web = new TextBlock { FontSize = Typography.Body };
         web.Inlines.Add(new Run(L("Web  ")) { FontWeight = FontWeights.SemiBold });
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

         panel.Children.Add(new Card
         {
            Title = L("Developed & maintained by"),
            Content = dev,
            Footer = Text(L("Copyright © 2026 Christopher Holloway / Progressive Robot Ltd. hMailServer is a trademark of its respective owners; this is an independent, community-maintained fork."),
               "TextCaptionTertiary", new Thickness(0))
         });

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

      /// <summary>A clickable hyperlink that opens in the default browser.</summary>
      private static TextBlock Link(string text, string url)
      {
         var tb = new TextBlock { FontSize = Typography.Body, TextWrapping = TextWrapping.Wrap };
         var hl = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url) };
         hl.RequestNavigate += (s, e) =>
         {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
         };
         tb.Inlines.Add(hl);
         return tb;
      }

      public void OnEnter()
      {
         try
         {
            serverVersion_.Text = F("Connected server: hMailServer {0} @ {1}", (string)ServerSession.Current.Application.Version, ServerSession.Current.Host);
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            serverVersion_.Text = "";
         }
      }

      public void OnLeave()
      {
      }
   }
}
