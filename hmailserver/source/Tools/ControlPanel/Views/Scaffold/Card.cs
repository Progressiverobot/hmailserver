// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace hMailServer.ControlPanel.Views.Scaffold
{
   /// <summary>
   /// A card: the raised surface a page groups things on. An optional title and
   /// description above the content, an optional footer under a hairline, the
   /// card surface and border from the tokens (AppCardBackgroundBrush,
   /// AppCardBorderBrush, AppCardCornerRadius, AppCardPadding). A card without
   /// a title is the plain surface the old <c>Card</c> Border style gave, which
   /// stays for the pages not yet migrated.
   ///
   /// To a screen reader the card is a group named by its title, so "Server"
   /// is heard before the five values inside it.
   ///
   /// <code>
   /// &lt;scaffold:Card Title="{loc:L 'Server'}" Description="{loc:L '...'}"&gt;
   ///    ...content...
   ///    &lt;scaffold:Card.Footer&gt;&lt;ui:Button .../&gt;&lt;/scaffold:Card.Footer&gt;
   /// &lt;/scaffold:Card&gt;
   /// </code>
   /// </summary>
   public class Card : ScaffoldContentControl
   {
      static Card()
      {
         DefaultStyleKeyProperty.OverrideMetadata(typeof(Card), new FrameworkPropertyMetadata(typeof(Card)));
      }

      public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
         nameof(Title), typeof(string), typeof(Card), new PropertyMetadata(null, OnTitleChanged, NullWhenEmpty));

      public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
         nameof(Description), typeof(string), typeof(Card), new PropertyMetadata(null, OnDescriptionChanged, NullWhenEmpty));

      public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
         nameof(Footer), typeof(object), typeof(Card), new PropertyMetadata(null));

      /// <summary>The card's heading. Null gives a plain surface.</summary>
      public string Title
      {
         get => (string)GetValue(TitleProperty);
         set => SetValue(TitleProperty, value);
      }

      /// <summary>One or two lines under the title saying what the card is for.</summary>
      public string Description
      {
         get => (string)GetValue(DescriptionProperty);
         set => SetValue(DescriptionProperty, value);
      }

      /// <summary>Content under a hairline at the bottom - the card's own actions or a note.</summary>
      public object Footer
      {
         get => GetValue(FooterProperty);
         set => SetValue(FooterProperty, value);
      }

      protected override AutomationControlType ControlType => AutomationControlType.Group;

      private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
         => AutomationProperties.SetName(d, (string)e.NewValue ?? "");

      private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
         => AutomationProperties.SetHelpText(d, (string)e.NewValue ?? "");
   }
}
