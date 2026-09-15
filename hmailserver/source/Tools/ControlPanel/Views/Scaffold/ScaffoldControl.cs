// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace hMailServer.ControlPanel.Views.Scaffold
{
   /// <summary>
   /// What every scaffold component shares: it is never a tab stop itself (its
   /// content is), an empty string set on one of its text slots means the same
   /// as no text (the templates collapse a slot on null, and a caller must be
   /// able to write <c>Subtitle = ""</c> and get the same as never setting it),
   /// and it exists in the automation tree as an element of its own with a name,
   /// which a bare <see cref="Control"/> does not - a Control has no automation
   /// peer, so a card, a notice or a pill built from one would reach a screen
   /// reader only as the loose text inside it.
   ///
   /// The templates live in Views/Scaffold/Scaffold.xaml, merged into App.xaml,
   /// as implicit styles keyed by the component type; each class points its
   /// default style key at itself so that no theme style of another control
   /// bleeds in underneath.
   /// </summary>
   public abstract class ScaffoldControl : Control
   {
      protected ScaffoldControl()
      {
         Focusable = false;
         IsTabStop = false;
      }

      /// <summary>The UI Automation control type this component reports.</summary>
      protected abstract AutomationControlType ControlType { get; }

      protected override AutomationPeer OnCreateAutomationPeer()
         => new ScaffoldAutomationPeer(this, ControlType);

      /// <summary>A coercion for the text slots: the empty string is null.</summary>
      protected static object NullWhenEmpty(DependencyObject d, object value)
         => value is string text && text.Length == 0 ? null : value;

      /// <summary>A named part of the applied template, or null before the template is applied.</summary>
      protected T Part<T>(string name) where T : class => GetTemplateChild(name) as T;
   }

   /// <summary>The same, for the components that carry content of their own: a card, a section, a field row, a notice.</summary>
   public abstract class ScaffoldContentControl : ContentControl
   {
      protected ScaffoldContentControl()
      {
         Focusable = false;
         IsTabStop = false;
      }

      protected abstract AutomationControlType ControlType { get; }

      protected override AutomationPeer OnCreateAutomationPeer()
         => new ScaffoldAutomationPeer(this, ControlType);

      protected static object NullWhenEmpty(DependencyObject d, object value)
         => value is string text && text.Length == 0 ? null : value;

      protected T Part<T>(string name) where T : class => GetTemplateChild(name) as T;
   }

   /// <summary>
   /// The peer the scaffold components report themselves through: a control
   /// element of the type the component names, with the name, help text and live
   /// setting read from the AutomationProperties the component sets on itself.
   /// </summary>
   internal sealed class ScaffoldAutomationPeer : FrameworkElementAutomationPeer
   {
      private readonly AutomationControlType type_;

      public ScaffoldAutomationPeer(FrameworkElement owner, AutomationControlType type)
         : base(owner)
      {
         type_ = type;
      }

      protected override AutomationControlType GetAutomationControlTypeCore() => type_;

      protected override string GetClassNameCore() => Owner.GetType().Name;

      protected override bool IsControlElementCore() => true;

      protected override bool IsContentElementCore() => true;
   }
}
