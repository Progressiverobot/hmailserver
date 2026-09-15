// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Shapes;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views.Scaffold
{
   /// <summary>
   /// One field of a form: the caption above the editor, the editor (the
   /// content), an optional hint under it, and an optional validation message
   /// under that. The caption carries an Alt-key mnemonic the way every caption
   /// here does ("_Host" is Alt+H), wired to the editor through
   /// <see cref="Mnemonic"/>; the editor takes its accessible name from the
   /// caption and its help text from the hint and the message, so a screen
   /// reader landing in the editor hears what it is, what it wants and what is
   /// wrong with it - the same three things a sighted user reads around it.
   ///
   /// The message is drawn with the danger colour, a cross and the words
   /// themselves; it is an assertive live region, so it is announced when it
   /// appears. Set <see cref="Error"/> to null to clear it.
   ///
   /// <code>
   /// &lt;scaffold:FieldRow Label="{loc:L '_Host'}" Hint="{loc:L 'The name or address of the server.'}"&gt;
   ///    &lt;ui:TextBox x:Name="HostBox" /&gt;
   /// &lt;/scaffold:FieldRow&gt;
   /// </code>
   /// </summary>
   public class FieldRow : ScaffoldContentControl
   {
      static FieldRow()
      {
         DefaultStyleKeyProperty.OverrideMetadata(typeof(FieldRow), new FrameworkPropertyMetadata(typeof(FieldRow)));
      }

      public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
         nameof(Label), typeof(string), typeof(FieldRow), new PropertyMetadata(null, OnAnyChanged, NullWhenEmpty));

      public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
         nameof(Hint), typeof(string), typeof(FieldRow), new PropertyMetadata(null, OnAnyChanged, NullWhenEmpty));

      public static readonly DependencyProperty ErrorProperty = DependencyProperty.Register(
         nameof(Error), typeof(string), typeof(FieldRow), new PropertyMetadata(null, OnAnyChanged, NullWhenEmpty));

      /// <summary>The caption, with its Alt key marked by an underscore.</summary>
      public string Label
      {
         get => (string)GetValue(LabelProperty);
         set => SetValue(LabelProperty, value);
      }

      /// <summary>What the field wants, in one line, shown under the editor.</summary>
      public string Hint
      {
         get => (string)GetValue(HintProperty);
         set => SetValue(HintProperty, value);
      }

      /// <summary>Why the value is not acceptable, or null when it is.</summary>
      public string Error
      {
         get => (string)GetValue(ErrorProperty);
         set => SetValue(ErrorProperty, value);
      }

      protected override AutomationControlType ControlType => AutomationControlType.Group;

      public override void OnApplyTemplate()
      {
         base.OnApplyTemplate();

         if (Part<Path>("PART_ErrorMark") is { } mark)
            ShapeMarkVisuals.ApplyMark(mark, StatusSemantics.For(StatusLevel.Critical).Shape, "AppDangerBrush");

         Apply_();
      }

      protected override void OnContentChanged(object oldContent, object newContent)
      {
         base.OnContentChanged(oldContent, newContent);
         Apply_();
      }

      private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
         => ((FieldRow)d).Apply_();

      private void Apply_()
      {
         string label = Label ?? "";
         var editor = Content as FrameworkElement;

         AutomationProperties.SetName(this, MnemonicText.Strip(label));

         if (Part<TextBlock>("PART_Label") is { } caption)
            Mnemonic.Apply(caption, label, editor);

         if (editor == null)
            return;

         // The editor's own name and help, unless the page gave it a name of its
         // own - a page that already says something more exact keeps it.
         if (string.IsNullOrEmpty(AutomationProperties.GetName(editor)) && label.Length > 0)
            AutomationProperties.SetName(editor, AccessibleNames.Qualify(MnemonicText.Strip(label), ""));

         string help = Hint ?? "";
         if (Error != null)
            help = help.Length == 0 ? Error : help + " " + Error;

         AutomationProperties.SetHelpText(editor, help);
      }
   }
}
