using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Draws a <see cref="ShapeMark"/> and a <see cref="ChartLinePattern"/> with WPF
   /// primitives.
   ///
   /// Deliberately not Fluent icons: geometry we control scales cleanly at the
   /// 200-400% display scaling a High Contrast user is often running, and a legend
   /// whose swatch is a rounded icon does not read as the same thing the chart drew.
   ///
   /// This comment used to claim the shapes "line up pixel-for-pixel with what Skia
   /// draws inside the chart", which was not true: the series are created with
   /// GeometrySize = 0, so Skia draws no markers at all. The legend no longer draws a
   /// marker either (see BuildLegend_ in AccessibleChartCard), so nothing here is
   /// currently used for series identity - the dash patterns carry that. MakeMark is
   /// kept because it is what a future change that gives the series real per-series
   /// geometry would draw against, and because the status badges use the same shapes.
   ///
   /// All geometries are built once and frozen: they are shared by every legend
   /// swatch and every status badge in the app.
   /// </summary>
   public static class ShapeMarkVisuals
   {
      // A 12x12 box, so a swatch and a badge can use the same geometry at
      // different sizes through Stretch.Fill.
      private static readonly Geometry CircleGeometry = Freeze_(new EllipseGeometry(new Point(6, 6), 5.5, 5.5));
      private static readonly Geometry SquareGeometry = Freeze_(new RectangleGeometry(new Rect(0.5, 0.5, 11, 11)));
      private static readonly Geometry TriangleGeometry = Freeze_(Geometry.Parse("M 6,0 L 12,11 L 0,11 Z"));
      private static readonly Geometry DiamondGeometry = Freeze_(Geometry.Parse("M 6,0 L 12,6 L 6,12 L 0,6 Z"));
      private static readonly Geometry CrossGeometry = Freeze_(Geometry.Parse("M 1,1 L 11,11 M 11,1 L 1,11"));

      /// <summary>The outline for one mark, in a nominal 12x12 box.</summary>
      public static Geometry GeometryFor(ShapeMark mark)
      {
         switch (mark)
         {
            case ShapeMark.Square:
               return SquareGeometry;
            case ShapeMark.Triangle:
               return TriangleGeometry;
            case ShapeMark.Diamond:
               return DiamondGeometry;
            case ShapeMark.Cross:
               return CrossGeometry;
            default:
               return CircleGeometry;
         }
      }

      /// <summary>
      /// True for marks that are a pair of lines rather than an outline, and so
      /// have to be stroked instead of filled. Filling a cross produces nothing at
      /// all, which is the sort of silent failure that survives review.
      /// </summary>
      public static bool IsStrokeOnly(ShapeMark mark) => mark == ShapeMark.Cross;

      /// <summary>
      /// A <see cref="Path"/> for one mark, already coloured.
      ///
      /// Nothing is done here to hide the shape from assistive technology because
      /// nothing needs to be: Shape has no automation peer, so it never reaches the
      /// UI Automation control view. The words beside it are what gets announced,
      /// which is the right way round - the shape is there for the reader who can
      /// see the badge but not tell its colour apart.
      /// </summary>
      public static Path MakeMark(ShapeMark mark, Brush brush, double size)
      {
         var path = new Path
         {
            Width = size,
            Height = size,
            Stretch = Stretch.Fill,
            VerticalAlignment = VerticalAlignment.Center
         };

         Shape_(path, mark);

         if (IsStrokeOnly(mark))
            path.Stroke = brush;
         else
            path.Fill = brush;

         return path;
      }

      /// <summary>
      /// Points an existing <see cref="Path"/> at one mark, taking its colour from
      /// an application resource key rather than a brush.
      ///
      /// The key matters: ThemeTokens republishes the status brushes on every theme
      /// change, so a badge that had been handed a resolved brush would keep the
      /// colour it was given and end up as the one element on the page still
      /// painted for the previous theme.
      /// </summary>
      public static void ApplyMark(Path path, ShapeMark mark, string brushKey)
      {
         if (path == null)
            return;

         Shape_(path, mark);

         if (IsStrokeOnly(mark))
         {
            path.ClearValue(Shape.FillProperty);
            path.SetResourceReference(Shape.StrokeProperty, brushKey);
         }
         else
         {
            path.ClearValue(Shape.StrokeProperty);
            path.SetResourceReference(Shape.FillProperty, brushKey);
         }
      }

      /// <summary>
      /// WPF dash intervals for a chart line pattern, or null for a solid line.
      ///
      /// WPF's StrokeDashArray is in multiples of the stroke thickness whereas
      /// Skia's dash effect is in device pixels, so the numbers have to be divided
      /// by the thickness to make the legend swatch show the same pattern as the
      /// line in the chart. Getting this wrong is not obvious from a screenshot -
      /// it just makes a dashed line look dotted in the legend, which quietly
      /// breaks the mapping the legend exists to provide.
      /// </summary>
      public static DoubleCollection DashArrayFor(ChartLinePattern pattern, double strokeThickness)
      {
         float[] intervals = ChartPalette.DashIntervalsFor(pattern);
         if (intervals == null)
            return null;

         double scale = strokeThickness > 0 ? strokeThickness : 1;
         var dashes = new DoubleCollection(intervals.Length);
         foreach (float interval in intervals)
            dashes.Add(interval / scale);

         dashes.Freeze();
         return dashes;
      }

      /// <summary>Converts a 0xAARRGGBB palette value to a WPF colour.</summary>
      public static Color ToColor(uint argb)
      {
         return Color.FromArgb((byte) (argb >> 24), (byte) (argb >> 16), (byte) (argb >> 8), (byte) argb);
      }

      /// <summary>A frozen brush for a 0xAARRGGBB palette value.</summary>
      public static SolidColorBrush ToBrush(uint argb)
      {
         var brush = new SolidColorBrush(ToColor(argb));
         brush.Freeze();
         return brush;
      }

      private static void Shape_(Path path, ShapeMark mark)
      {
         path.Data = GeometryFor(mark);

         // A cross is two line segments with nothing to fill, so it only exists at
         // all if it is stroked. The stroke has to be cleared again for the closed
         // shapes or a filled triangle picks up a heavy outline and stops matching
         // the size of the other marks.
         path.StrokeThickness = IsStrokeOnly(mark) ? 2 : 0;
      }

      private static Geometry Freeze_(Geometry geometry)
      {
         geometry.Freeze();
         return geometry;
      }
   }
}
