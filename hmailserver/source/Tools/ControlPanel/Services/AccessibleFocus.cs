using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Installs a keyboard focus ring that is visible on every theme.
   ///
   /// WPF's stock focus visual is a one-pixel black dotted rectangle. On the dark
   /// theme that is black on near-black and effectively invisible; on a High
   /// Contrast Black theme it disappears completely. Keyboard users are the whole
   /// audience for a focus ring, so an invisible one is a functional bug, not a
   /// cosmetic one.
   ///
   /// The replacement is the standard two-tone ring: a solid dark rectangle with a
   /// dashed light rectangle drawn on top of it. The pair cannot vanish, because
   /// whichever of the two colours matches the background, the other one does not
   /// - on white the black solid line carries it, on black the white dashes do,
   /// and on a mid-tone surface both are visible. That is why it is done with two
   /// fixed colours rather than a theme brush: a ring computed from the current
   /// theme is only as good as our guess about what is behind it, and the chart
   /// cards, the log viewer and the nav sidebar all have different backgrounds.
   ///
   /// Registered under <see cref="SystemParameters.FocusVisualStyleKey"/> in the
   /// application resources, which is the documented hook for replacing the focus
   /// visual for every control that has not opted out of it. WPF-UI's own controls
   /// draw their own focus ring and set FocusVisualStyle to null, so they are
   /// unaffected; this fixes the stock controls (data grids, list boxes, the
   /// tree view, plain buttons) which is where the gap actually was.
   ///
   /// It is registered from code rather than from App.xaml so that the whole
   /// feature is one file, and because App.xaml is shared with the navigation and
   /// palette work.
   /// </summary>
   public static class AccessibleFocus
   {
      private static bool registered_;

      /// <summary>Safe to call more than once; does nothing without an Application.</summary>
      public static void Register()
      {
         if (registered_ || Application.Current == null)
            return;

         registered_ = true;
         Application.Current.Resources[SystemParameters.FocusVisualStyleKey] = BuildStyle_();
      }

      private static Style BuildStyle_()
      {
         // Frozen: a FrameworkElementFactory hands the same value to every
         // instantiation of the template, and every focused control in the app
         // shares this one template.
         var dashes = new DoubleCollection(new[] { 2.0, 2.0 });
         dashes.Freeze();

         var tree = new FrameworkElementFactory(typeof(Grid));
         tree.AppendChild(Ring_(Brushes.Black, null));
         tree.AppendChild(Ring_(Brushes.White, dashes));

         var template = new ControlTemplate(typeof(Control)) { VisualTree = tree };

         // The focus adorner hosts a Control and applies this style to it, so the
         // template property has to be set as Control.Template.
         var style = new Style(typeof(Control));
         style.Setters.Add(new Setter(Control.TemplateProperty, template));
         style.Seal();
         return style;
      }

      private static FrameworkElementFactory Ring_(Brush stroke, DoubleCollection dashes)
      {
         var ring = new FrameworkElementFactory(typeof(Rectangle));
         ring.SetValue(Shape.StrokeProperty, stroke);
         ring.SetValue(Shape.StrokeThicknessProperty, 2.0);
         ring.SetValue(Rectangle.RadiusXProperty, 4.0);
         ring.SetValue(Rectangle.RadiusYProperty, 4.0);

         // Sit just outside the focused element. The adorner layer does not clip,
         // and a ring drawn inside the bounds gets lost against the control's own
         // border on the Fluent controls.
         ring.SetValue(FrameworkElement.MarginProperty, new Thickness(-2));
         ring.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

         if (dashes != null)
            ring.SetValue(Shape.StrokeDashArrayProperty, dashes);

         return ring;
      }
   }
}
