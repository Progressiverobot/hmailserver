namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// The Control Panel type ramp. Centralises the font sizes that were
   /// previously scattered as inline literals so the scale is defined in one
   /// place and stays consistent. Page-level roles (page title/subtitle, KPI
   /// values, status labels) live as named styles in App.xaml; these constants
   /// cover the body/label/heading sizes used by the code-behind views and
   /// dialogs.
   /// </summary>
   public static class Typography
   {
      /// <summary>Small caption / hint text (e.g. card blurbs, status lines).</summary>
      public const double Caption = 12;

      /// <summary>Secondary body text and most field labels in dialogs.</summary>
      public const double Label = 12.5;

      /// <summary>Default body text and input-control text.</summary>
      public const double Body = 13;

      /// <summary>Checkboxes and slightly emphasised body text.</summary>
      public const double Control = 13.5;

      /// <summary>List/item titles.</summary>
      public const double ItemTitle = 14;

      /// <summary>Card / section headings inside a page.</summary>
      public const double SectionHeading = 15;

      /// <summary>Modal dialog header.</summary>
      public const double DialogTitle = 18;
   }
}
