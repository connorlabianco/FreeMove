// FreeMove -- Move directories without breaking shortcuts or installations
//    Copyright(C) 2020  Luca De Martini
//
//    This program is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.

using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace FreeMove.Dialogs
{
    /// <summary>
    /// A drop-in for MessageBox.Show for messages that can get long (summaries, file lists,
    /// error lists). Short messages use the normal Windows message box. Long ones open a
    /// resizable window with a scrollable, selectable text area that never grows past the screen.
    /// </summary>
    public static class ScrollableMessageBox
    {
        const int MaxShortLines = 18;
        const int MaxShortChars = 1200;

        public static DialogResult Show(IWin32Window owner, string text, string caption,
            MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None,
            MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
        {
            text = text ?? "";
            int lines = text.Split('\n').Length;
            if (lines <= MaxShortLines && text.Length <= MaxShortChars)
                return MessageBox.Show(owner, text, caption, buttons, icon, defaultButton);

            using (var form = new LongMessageForm(text, caption, buttons, icon, defaultButton))
                return form.ShowDialog(owner);
        }

        class LongMessageForm : Form
        {
            static string T(string key, string fallback) =>
                Properties.Resources.ResourceManager.GetString(key) ?? fallback;

            public LongMessageForm(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
            {
                Text = caption;
                StartPosition = FormStartPosition.CenterParent;
                ShowIcon = false;
                ShowInTaskbar = false;
                MinimizeBox = false;
                FormBorderStyle = FormBorderStyle.Sizable;
                Font = SystemFonts.MessageBoxFont;

                // Size to the text, but never larger than 80% of the screen
                Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
                MinimumSize = new Size(420, 260);
                Size = new Size(Math.Min(760, area.Width * 8 / 10), Math.Min(560, area.Height * 8 / 10));

                var textBox = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Dock = DockStyle.Fill,
                    BackColor = SystemColors.Window,
                    Text = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
                    TabStop = false
                };

                var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8, 8, 8, 4) };
                Icon systemIcon = IconFor(icon);
                if (systemIcon != null)
                {
                    top.Controls.Add(new PictureBox { Image = systemIcon.ToBitmap(), SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 0, 8, 0) }, 0, 0);
                }
                top.Controls.Add(new Label
                {
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Text = T("LongMsg_Hint", "Scroll to read everything. You can resize this window and copy the text.")
                }, 1, 0);

                var bottom = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    AutoSize = true,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(8, 4, 8, 8)
                };

                Button[] made = MakeButtons(buttons);
                for (int i = made.Length - 1; i >= 0; i--) bottom.Controls.Add(made[i]);
                
                // Copy everything, handy for reporting issues
                var copy = new Button { Text = T("LongMsg_Copy", "Copy"), AutoSize = true };
                copy.Click += (s, e) => { try { Clipboard.SetText(textBox.Text); } catch { } };
                bottom.Controls.Add(copy);

                var middle = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 0) };
                middle.Controls.Add(textBox);

                Controls.Add(middle);
                Controls.Add(bottom);
                Controls.Add(top);

                int defaultIndex = defaultButton == MessageBoxDefaultButton.Button2 ? 1 : defaultButton == MessageBoxDefaultButton.Button3 ? 2 : 0;
                if (defaultIndex < made.Length)
                {
                    AcceptButton = made[defaultIndex];
                    Shown += (s, e) => made[defaultIndex].Focus();
                }
                Button cancel = made.FirstOrDefault(b => b.DialogResult == DialogResult.Cancel || b.DialogResult == DialogResult.No)
                                ?? made.FirstOrDefault(b => b.DialogResult == DialogResult.OK);
                if (cancel != null) CancelButton = cancel;
            }

            static Button[] MakeButtons(MessageBoxButtons buttons)
            {
                Button B(string key, string fallback, DialogResult result) =>
                    new Button { Text = T(key, fallback), DialogResult = result, AutoSize = true, MinimumSize = new Size(80, 0) };

                switch (buttons)
                {
                    case MessageBoxButtons.YesNo:
                        return new[] { B("LongMsg_Yes", "Yes", DialogResult.Yes), B("LongMsg_No", "No", DialogResult.No) };
                    case MessageBoxButtons.YesNoCancel:
                        return new[] { B("LongMsg_Yes", "Yes", DialogResult.Yes), B("LongMsg_No", "No", DialogResult.No), B("Button_Cancel", "Cancel", DialogResult.Cancel) };
                    case MessageBoxButtons.OKCancel:
                        return new[] { B("LongMsg_OK", "OK", DialogResult.OK), B("Button_Cancel", "Cancel", DialogResult.Cancel) };
                    default:
                        return new[] { B("LongMsg_OK", "OK", DialogResult.OK) };
                }
            }

            static Icon IconFor(MessageBoxIcon icon)
            {
                switch (icon)
                {
                    case MessageBoxIcon.Error: return SystemIcons.Error;
                    case MessageBoxIcon.Warning: return SystemIcons.Warning;
                    case MessageBoxIcon.Question: return SystemIcons.Question;
                    case MessageBoxIcon.Information: return SystemIcons.Information;
                    default: return null;
                }
            }
        }
    }
}
