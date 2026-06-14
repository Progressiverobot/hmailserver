namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Shared validation for optional numeric dialog fields. An empty box means
   /// "leave the current value unchanged" and is always valid; non-empty text must
   /// parse to a whole number inside the allowed range, otherwise it is reported
   /// instead of being silently ignored.
   /// </summary>
   public static class NumericField
   {
      public static bool TryValidate(string text, string label, int min, int max,
                                     out int value, out bool hasValue, out string error)
      {
         value = 0;
         hasValue = false;
         error = null;

         string trimmed = text?.Trim() ?? string.Empty;
         if (trimmed.Length == 0)
            return true;

         if (!int.TryParse(trimmed, out int parsed))
         {
            error = label + " must be a whole number.";
            return false;
         }

         if (parsed < min || parsed > max)
         {
            error = string.Format("{0} must be between {1} and {2}.", label, min, max);
            return false;
         }

         value = parsed;
         hasValue = true;
         return true;
      }
   }
}
