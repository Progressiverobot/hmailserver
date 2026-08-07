// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Threading;

namespace hMailServer.Shared
{
    public partial class formErrorMessage : Form
    {
        private Exception _exception;
        private UnhandledExceptionEventArgs _args;
        private ThreadExceptionEventArgs _threadArgs;

        private string _title;
        private string _errorMessage;

        public formErrorMessage(UnhandledExceptionEventArgs args)
        {
            _args = args;

            InitializeComponent();
        }

        public formErrorMessage(ThreadExceptionEventArgs args)
        {
            _threadArgs = args;

            InitializeComponent();
        }

        public formErrorMessage(Exception exception, string title, string errorMessage)
        {
            _exception = exception;
            _title = title;
            _errorMessage = errorMessage;

            InitializeComponent();
        }

        private void buttonClose_Click(object sender, EventArgs e)
        {
            if (_args != null)
            {
                // Global exception. Exit application.
                System.Windows.Forms.Application.Exit();
            }
            else
            {
                this.Close();
            }
        }

        private void formErrorMessage_Shown(object sender, EventArgs e)
        {

            if (_exception == null)
            {
                if (_args == null)
                    _exception = ((Exception)(_threadArgs.Exception));
                else
                    _exception = ((Exception)(_args.ExceptionObject));
            }

            if (string.IsNullOrEmpty(_errorMessage))
                textErrorMessage.Text = _exception.Message;
            else
                textErrorMessage.Text = _errorMessage;

            if (!string.IsNullOrEmpty(_title))
                this.Text = _title;

            var details = new StringBuilder();
            details.Append(_exception.Message).Append(Environment.NewLine).Append(Environment.NewLine);

            AppendException(details, _exception, string.Empty);

            string indent = "	";
            Exception ie = _exception;
            while (ie.InnerException != null)
            {
                ie = ie.InnerException;
                details.Append(indent).Append("****** Inner Exception ******").Append(Environment.NewLine);
                AppendException(details, ie, indent);
                indent += "	";
            }

            textErrorDetails.Text = details.ToString();
        }

        // Built with a StringBuilder rather than repeated string concatenation, and
        // shared between the outer exception and each inner one - the inner-exception
        // copy of this block had "+ Environment.NewLine" inside the format string, so
        // the StackTrace and TargetSite lines printed that text instead of a newline.
        private static void AppendException(StringBuilder details, Exception exception, string indent)
        {
            details.Append(indent).Append("ExceptionType: ").Append(exception.GetType().Name).Append(Environment.NewLine);
            details.Append(indent).Append("HelpLine: ").Append(exception.HelpLink).Append(Environment.NewLine);
            details.Append(indent).Append("Message: ").Append(exception.Message).Append(Environment.NewLine);
            details.Append(indent).Append("Source: ").Append(exception.Source).Append(Environment.NewLine);
            details.Append(indent).Append("StackTrace: ").Append(exception.StackTrace).Append(Environment.NewLine);
            details.Append(indent).Append("TargetSite: ").Append(exception.TargetSite).Append(Environment.NewLine);
        }
    }
}
