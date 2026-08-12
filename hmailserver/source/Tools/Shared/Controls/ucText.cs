// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace hMailServer.Shared
{
   public partial class ucText : TextBox, IPropertyEditor
   {
      private string internalText;
      private bool _numeric;
      public ucText()
      {
         internalText = "";
         _numeric = false;

      }

      // The designer serialization visibility on the four public properties below
      // is declared explicitly because .NET 9 added the WFO1000 analyzer, which
      // (correctly) refuses to guess. Text and Numeric are real, designer-settable
      // state and are serialized; Number and Number64 are derived views over Text
      // and must not be, because the designer was previously emitting meaningless
      // "Number = 0" / "Number64 = 0" lines into generated code. Existing generated
      // lines still compile - this only changes what the designer writes from now on.
      [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
      public new string Text
      {
         get
         {
            return base.Text.Trim();
         }

         set
         {
            base.Text = value;
            internalText = value;
         }
      }

      [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
      [Browsable(false)]
      public int Number
      {
         get
         {
            if (!_numeric || Text == "")
               return 0;

            return Convert.ToInt32(Text);
         }
         set
         {
            if (!_numeric)
               return;

            Text = value.ToString();
         }
      }

      [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
      [Browsable(false)]
      public Int64 Number64
      {
         get
         {
            if (!_numeric || Text == "")
               return 0;

            return Convert.ToInt64(Text);
         }
         set
         {
            if (!_numeric)
               return;

            Text = value.ToString();
         }
      }

      public bool Dirty
      {
         get
         {
            return base.Text != internalText;

         }
      }

      public void SetClean()
      {
         internalText = base.Text;
      }

      [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
      [DefaultValue(false)]
      public bool Numeric
      {
         get
         {
            return _numeric;
         }
         set
         {
            _numeric = value;
         }
      }

      protected override void OnKeyPress(KeyPressEventArgs e)
      {
         if (_numeric && !char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
            e.Handled = true;

         base.OnKeyPress(e);
      }

   }
}
