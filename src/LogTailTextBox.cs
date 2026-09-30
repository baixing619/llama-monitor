using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LlamaMonitor
{
    /// <summary>Refreshes the bounded log buffer without losing the reader's place.</summary>
    internal sealed class LogTailTextBox : TextBox
    {
        private const int EmGetFirstVisibleLine = 0x00CE;
        private const int EmLineScroll = 0x00B6;
        private const int SbVert = 1;
        private const uint SifRange = 0x0001;
        private const uint SifPage = 0x0002;
        private const uint SifPos = 0x0004;
        private bool _scrollToLatestOnCreate;

        [StructLayout(LayoutKind.Sequential)]
        private struct ScrollInfo
        {
            public uint cbSize;
            public uint fMask;
            public int nMin;
            public int nMax;
            public uint nPage;
            public int nPos;
            public int nTrackPos;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref ScrollInfo info);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

        /// <param name="removedChars">Characters removed from the front of the previous buffer.</param>
        /// <param name="resetView">Start following the end after switching or clearing logs.</param>
        public void ReplaceLogText(string text, int removedChars, bool resetView)
        {
            text = text ?? string.Empty;
            removedChars = Math.Max(0, removedChars);

            int selectionStart = SelectionStart;
            int selectionEnd = selectionStart + SelectionLength;
            bool follow = resetView || (SelectionLength == 0 && IsAtBottom());
            int firstVisibleChar = 0;
            if (IsHandleCreated)
            {
                int firstLine = FirstVisibleLine();
                firstVisibleChar = Math.Max(0, GetFirstCharIndexFromLine(firstLine));
            }

            Text = text;
            if (follow)
            {
                JumpToLatest();
                return;
            }

            // Shift both ends separately: a selection partly trimmed away keeps its surviving text.
            int start = AdjustIndex(selectionStart, removedChars, TextLength);
            int end = AdjustIndex(selectionEnd, removedChars, TextLength);
            Select(start, Math.Max(0, end - start));

            if (IsHandleCreated)
            {
                // A character anchor also handles a trim in the middle of a line or wrapped lines.
                int anchor = AdjustIndex(firstVisibleChar, removedChars, TextLength);
                int targetLine = GetLineFromCharIndex(anchor);
                SendMessage(Handle, EmLineScroll, IntPtr.Zero,
                    new IntPtr(targetLine - FirstVisibleLine()));
            }
            _scrollToLatestOnCreate = false;
        }

        public void JumpToLatest()
        {
            Select(TextLength, 0);
            if (IsHandleCreated)
            {
                ScrollToCaret();
                // ScrollToCaret can do nothing before the control is shown or gains focus.
                var info = NewScrollInfo();
                if (GetScrollInfo(Handle, SbVert, ref info))
                {
                    int bottom = BottomPosition(info);
                    SendMessage(Handle, EmLineScroll, IntPtr.Zero,
                        new IntPtr(bottom - FirstVisibleLine()));
                }
                _scrollToLatestOnCreate = !Visible;
            }
            else _scrollToLatestOnCreate = true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_scrollToLatestOnCreate) JumpToLatest();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && IsHandleCreated && _scrollToLatestOnCreate) JumpToLatest();
        }

        private int FirstVisibleLine()
        {
            return SendMessage(Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32();
        }

        private bool IsAtBottom()
        {
            if (!IsHandleCreated || TextLength == 0) return true;
            var info = NewScrollInfo();
            if (GetScrollInfo(Handle, SbVert, ref info))
            {
                int bottom = BottomPosition(info);
                // The thumb's nPos can lag behind the actual view during thumb tracking.
                return info.nPos >= bottom && FirstVisibleLine() >= bottom;
            }

            // No scroll bar information is available when the whole buffer fits in the control.
            int visibleLines = Math.Max(1, (ClientSize.Height - 2) / Math.Max(1, Font.Height));
            return FirstVisibleLine() + visibleLines > GetLineFromCharIndex(TextLength);
        }

        private static ScrollInfo NewScrollInfo()
        {
            return new ScrollInfo
            {
                cbSize = (uint)Marshal.SizeOf(typeof(ScrollInfo)),
                fMask = SifRange | SifPage | SifPos
            };
        }

        private static int BottomPosition(ScrollInfo info)
        {
            return (int)Math.Max(info.nMin, (long)info.nMax - Math.Max(0L, (long)info.nPage - 1));
        }

        private static int AdjustIndex(int index, int removedChars, int length)
        {
            return Math.Min(length, Math.Max(0, index - removedChars));
        }
    }
}
