using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace skdl_new_2025_test_tool
{
    public partial class AutoScaleForm : Form
    {
        private struct ControlRect
        {
            public int Left;
            public int Top;
            public int Width;
            public int Height;
            public float FontSize;
            public int OriginalWidth;
        }

        private Dictionary<Control, ControlRect> _controlCache = new Dictionary<Control, ControlRect>();
        private float _originalFormWidth;
        private float _originalFormHeight;
        private bool _isLoaded = false;
        private bool _isResizing = false;
        private int _lastScaleTick = 0;
        private const int ScaleThrottleMs = 50;

        private const int WM_SETREDRAW = 0x000B;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // === 优化1：定义需要进行文字自适应计算的控件类型 ===
        // 只有这些控件才需要防止文字截断，其他控件（如Panel）不需要耗时的测量
        private readonly HashSet<Type> _textControlTypes = new HashSet<Type>
        {
            typeof(Label),
            typeof(Button),
            typeof(CheckBox),
            typeof(RadioButton),
            typeof(LinkLabel)
        };

        public AutoScaleForm()
        {
            this.SetStyle(ControlStyles.UserPaint |
                          ControlStyles.AllPaintingInWmPaint |
                          ControlStyles.OptimizedDoubleBuffer, true);
            this.UpdateStyles();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _originalFormWidth = this.Width;
            _originalFormHeight = this.Height;
            RecordOriginalControlInfo(this);
            _isLoaded = true;
        }

        private void RecordOriginalControlInfo(Control parent)
        {
            foreach (Control con in parent.Controls)
            {
                if (!_controlCache.ContainsKey(con))
                {
                    _controlCache.Add(con, new ControlRect
                    {
                        Left = con.Left,
                        Top = con.Top,
                        Width = con.Width,
                        Height = con.Height,
                        FontSize = con.Font.Size,
                        OriginalWidth = con.Width
                    });
                }

                if (con.Controls.Count > 0)
                {
                    RecordOriginalControlInfo(con);
                }
            }
        }

        // === 优化2：缩放逻辑 ===
        private void ScaleControls(Control parent, float scaleX, float scaleY, bool scaleFonts)
        {
            // 预计算缩放因子，避免循环内重复计算
            float scaleFactor = Math.Min(scaleX, scaleY);

            foreach (Control con in parent.Controls)
            {
                if (_controlCache.TryGetValue(con, out ControlRect rect))
                {
                    // 1. 设置边界 (整数运算很快)
                    int newLeft = (int)(rect.Left * scaleX);
                    int newTop = (int)(rect.Top * scaleY);
                    int newWidth = (int)(rect.Width * scaleX);
                    int newHeight = (int)(rect.Height * scaleY);

                    if (con.Left != newLeft || con.Top != newTop || con.Width != newWidth || con.Height != newHeight)
                    {
                        con.SetBounds(newLeft, newTop, newWidth, newHeight);
                    }

                    if (scaleFonts)
                    {
                        // 2. 计算目标字体大小
                        float targetSize = rect.FontSize * scaleFactor;

                        // 3. 应用字体 (仅当差异较大时才应用，减少重绘)
                        if (targetSize > 0 && Math.Abs(con.Font.Size - targetSize) > 0.25f)
                        {
                            con.Font = new Font(con.Font.FontFamily, targetSize, con.Font.Style, con.Font.Unit);
                        }
                    }
                }

                if (con.Controls.Count > 0)
                {
                    ScaleControls(con, scaleX, scaleY, scaleFonts);
                }
            }
        }

        /// <summary>
        /// 快速计算最佳字号
        /// </summary>
        private float GetBestFitFontSize(Control con, float targetSize, int ctrlWidth)
        {
            int safeWidth = ctrlWidth - 6; // 稍微减小 Padding，提升一点计算速度
            if (safeWidth <= 0) return targetSize;

            try
            {
                // 创建临时字体进行测量 (这是最耗时的步骤)
                using (Font testFont = new Font(con.Font.FontFamily, targetSize, con.Font.Style))
                {
                    // 使用 TextFormatFlags 优化测量性能 (比默认 MeasureText 快一点点)
                    Size textSize = TextRenderer.MeasureText(con.Text, testFont, Size.Empty, TextFormatFlags.NoPadding);

                    if (textSize.Width > safeWidth)
                    {
                        float ratio = (float)safeWidth / (float)textSize.Width;
                        return Math.Max(targetSize * ratio, 6.0f); // 限制最小字号为 6
                    }
                }
            }
            catch
            {
                // 忽略测量异常，返回原值
            }

            return targetSize;
        }

        protected override void OnResize(EventArgs e)
        {
            if (!_isLoaded || _originalFormWidth == 0 || _originalFormHeight == 0) return;

            // 最小化时不计算
            if (WindowState == FormWindowState.Minimized) return;

            float scaleX = (float)this.Width / _originalFormWidth;
            float scaleY = (float)this.Height / _originalFormHeight;

            if (_isResizing)
            {
                int nowTick = Environment.TickCount;
                if (nowTick - _lastScaleTick < ScaleThrottleMs) return;
                _lastScaleTick = nowTick;
            }

            ApplyScale(scaleX, scaleY, !_isResizing);
        }

        protected override void OnResizeBegin(EventArgs e)
        {
            base.OnResizeBegin(e);
            _isResizing = true;
            _lastScaleTick = 0;
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            _isResizing = false;

            if (_isLoaded && _originalFormWidth > 0 && _originalFormHeight > 0 && WindowState != FormWindowState.Minimized)
            {
                float scaleX = (float)this.Width / _originalFormWidth;
                float scaleY = (float)this.Height / _originalFormHeight;
                ApplyScale(scaleX, scaleY, true);
            }
        }

        private void ApplyScale(float scaleX, float scaleY, bool scaleFonts)
        {
            if (FormBorderStyle != FormBorderStyle.None)
            {
                FormBorderStyle = FormBorderStyle.None;
            }

            SuspendLayout();
            try
            {
                if (IsHandleCreated)
                {
                    SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
                }

                ScaleControls(this, scaleX, scaleY, scaleFonts);
            }
            finally
            {
                if (IsHandleCreated)
                {
                    SendMessage(Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                }

                ResumeLayout(false);
                // 仅在最大化或还原操作结束时强制重绘，减少拖动时的闪烁
                if (WindowState == FormWindowState.Maximized || WindowState == FormWindowState.Normal)
                {
                    PerformLayout();
                    Invalidate(true);
                }
            }
        }
    }
}
