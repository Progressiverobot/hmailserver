// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows.Markup;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// <c>{loc:L 'Connect to server'}</c> - the XAML face of <see cref="Loc.L"/>.
   /// The text is looked up once, when the page loads, which is all a language
   /// choice needs: it is fixed for the life of the process (changing it restarts
   /// the Control Panel), so there is nothing for a binding to track. Quote the
   /// text with single quotes so that commas and equals signs inside it are text
   /// and not markup-extension syntax; a single quote inside is written \'.
   /// </summary>
   [MarkupExtensionReturnType(typeof(string))]
   public sealed class LExtension : MarkupExtension
   {
      public LExtension()
      {
      }

      public LExtension(string text)
      {
         Text = text;
      }

      /// <summary>The English text, exactly as it appears in the catalogue.</summary>
      [ConstructorArgument("text")]
      public string Text { get; set; }

      public override object ProvideValue(IServiceProvider serviceProvider) => Loc.L(Text ?? string.Empty);
   }
}
