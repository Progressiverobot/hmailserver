using System.Collections.Generic;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>How much attention a value is asking for.</summary>
   public enum StatusLevel
   {
      /// <summary>Nothing to say. Renders with no badge at all.</summary>
      Normal,
      Good,
      Information,
      Warning,
      Critical
   }

   /// <summary>
   /// The three things a status has to carry so that none of them is load-bearing
   /// on its own: a colour token, a shape, and a word.
   /// </summary>
   public sealed class StatusPresentation
   {
      internal StatusPresentation(StatusLevel level, string severityWord, ShapeMark shape, string brushKey)
      {
         Level = level;
         SeverityWord = severityWord;
         Shape = shape;
         BrushKey = brushKey;
      }

      public StatusLevel Level { get; }

      /// <summary>
      /// "Warning", "Critical", ... - prefixed to the caller's own text in the
      /// accessible name, so a screen reader hears the severity even though the
      /// visible badge only shows the shape and the caller's wording.
      /// </summary>
      public string SeverityWord { get; }

      /// <summary>
      /// The shape drawn beside the value. This is the channel that survives when
      /// the colour does not: printed in greyscale, viewed by a reader with
      /// deuteranopia, or squashed onto the two or three colours a High Contrast
      /// theme allows.
      /// </summary>
      public ShapeMark Shape { get; }

      /// <summary>
      /// Application resource key of the brush, e.g. "AppWarningBrush". A key
      /// rather than a brush because ThemeTokens republishes these on every theme
      /// change, so anything holding a resolved brush would keep the old colour.
      /// </summary>
      public string BrushKey { get; }
   }

   /// <summary>
   /// Maps a status level onto the colour token, the shape and the word that
   /// present it.
   ///
   /// The dashboard used to turn the queue counter amber above a hundred messages
   /// and leave it at that: the entire message was the colour, so it did not exist
   /// for anyone who could not see the colour, and the threshold was a bare
   /// literal in the middle of a refresh method. Both problems are fixed by having
   /// exactly one place that decides what a level looks like and what counts as
   /// which level.
   ///
   /// Every level gets a distinct shape - that invariant is what a test can hold
   /// on to, and it is the whole reason the shape is worth having.
   /// </summary>
   public static class StatusSemantics
   {
      /// <summary>
      /// Queue length at which the dashboard starts calling the queue a backlog.
      /// The number is the one the dashboard has always used; naming it here is
      /// what makes it testable and what stops the next reader guessing whether
      /// the comparison was inclusive.
      /// </summary>
      public const int QueueBacklogThreshold = 100;

      private static readonly Dictionary<StatusLevel, StatusPresentation> Map = new()
      {
         [StatusLevel.Normal] =
            new StatusPresentation(StatusLevel.Normal, "Normal", ShapeMark.Circle, "TextFillColorPrimaryBrush"),
         [StatusLevel.Good] =
            new StatusPresentation(StatusLevel.Good, "OK", ShapeMark.Square, "AppSuccessBrush"),
         [StatusLevel.Information] =
            new StatusPresentation(StatusLevel.Information, "Information", ShapeMark.Diamond, "AppInfoBrush"),
         [StatusLevel.Warning] =
            new StatusPresentation(StatusLevel.Warning, "Warning", ShapeMark.Triangle, "AppWarningBrush"),
         [StatusLevel.Critical] =
            new StatusPresentation(StatusLevel.Critical, "Critical", ShapeMark.Cross, "AppDangerBrush")
      };

      /// <summary>Never returns null; an unknown level is treated as Normal.</summary>
      public static StatusPresentation For(StatusLevel level)
      {
         return Map.TryGetValue(level, out StatusPresentation presentation) ? presentation : Map[StatusLevel.Normal];
      }

      /// <summary>Every level, in increasing severity. Only used by the tests today.</summary>
      public static IReadOnlyList<StatusLevel> AllLevels { get; } = new[]
      {
         StatusLevel.Normal,
         StatusLevel.Good,
         StatusLevel.Information,
         StatusLevel.Warning,
         StatusLevel.Critical
      };

      /// <summary>
      /// Whether a queue of this length is a backlog. Strictly greater than the
      /// threshold, matching the behaviour the dashboard has always had: a queue
      /// sitting on exactly a hundred messages is busy, not broken.
      /// </summary>
      public static StatusLevel ForQueueLength(int queueLength, int threshold = QueueBacklogThreshold)
      {
         return queueLength > threshold ? StatusLevel.Warning : StatusLevel.Normal;
      }

      /// <summary>
      /// Every severity word the Server status page prints on a configuration
      /// warning. Here so a test can walk the whole set rather than spot-check the
      /// three the author happened to think of.
      /// </summary>
      public static IReadOnlyList<string> ConfigurationWarningSeverities { get; } = new[]
      {
         "Critical", "High", "Medium", "Info"
      };

      /// <summary>
      /// Maps one of those words onto the single status vocabulary the rest of the
      /// application uses.
      ///
      /// WHY THIS EXISTS: the warnings panel on the Server status page had its own
      /// private severity scale and, with it, its own private colour table - four
      /// hardcoded RGB values with <c>Brushes.White</c> text on top - and that
      /// carried three separate defects.
      ///
      /// The colours never consulted the theme, so a High Contrast desktop got
      /// exactly the four colours the user had just told Windows they could not
      /// read, on the one page that says "this server may be an open relay".
      ///
      /// White on the "Medium" amber #C28A00 measures 3.03:1 and white on the
      /// "High" orange #D24F1A measures 4.31:1, against the 4.5:1 that eleven-point
      /// text needs - so two of the four badges failed WCAG AA in every theme,
      /// including the two everybody runs. That was found by computing the ratios,
      /// not by looking at them; by eye all four look perfectly confident.
      ///
      /// And the colour table's default arm returned the calmest of the four, so a
      /// severity word that did not match anything - a typo, or a word added at a
      /// new call site - rendered as the least alarming badge on the page and
      /// nothing anywhere said so. That is the ignored-return-value defect wearing
      /// a different hat: a failure the representation could not express.
      ///
      /// Anything unrecognised is therefore a <see cref="StatusLevel.Warning"/>,
      /// never <see cref="StatusLevel.Normal"/>: Normal renders as ordinary body
      /// text with no badge at all, and a warning that renders as body text is a
      /// warning that is not shown.
      /// </summary>
      public static StatusLevel ForConfigurationWarning(string severity)
      {
         if (string.IsNullOrWhiteSpace(severity))
            return StatusLevel.Warning;

         switch (severity.Trim().ToLowerInvariant())
         {
            case "critical":
               return StatusLevel.Critical;

            // "High" is a misconfiguration the administrator has to act on.
            case "high":
            case "warning":
               return StatusLevel.Warning;

            // "Medium" is only ever used for a count of auto-ban ranges, and
            // "Info" for "some checks could not be evaluated". Neither is a defect
            // in the configuration, and collapsing them removes a distinction the
            // page never actually made use of.
            case "medium":
            case "info":
            case "information":
               return StatusLevel.Information;

            default:
               return StatusLevel.Warning;
         }
      }
   }
}
