// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Net.Mail;

namespace TestBedUI
{
    public partial class formMain : Form
    {
        public formMain()
        {
            InitializeComponent();
        }

        private void buttonSend_Click(object sender, EventArgs e)
        {
            try
            {
                using (var message = new MailMessage())
                using (var client = new SmtpClient())
                {
                    message.From = new MailAddress(textFrom.Text);
                    message.To.Add(textTo.Text);
                    message.Subject = textSubject.Text;

                    client.Host = "localhost";
                    client.Send(message);
                }
            }
            catch(Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void buttonSendWithEICAR_Click(object sender, EventArgs e)
        {
            try
            {
                using (var message = new MailMessage())
                using (var client = new SmtpClient())
                {
                    message.From = new MailAddress(textFrom.Text);
                    message.To.Add(textTo.Text);
                    message.Subject = textSubject.Text;
                    message.Body = @"X5O!P%@AP[4\PZX54(P^)7CC)7}" + @"$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";
                    client.Host = "localhost";
                    client.Send(message);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }
    }
}
