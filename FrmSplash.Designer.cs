namespace skdl_new_2025_test_tool
{
    partial class FrmSplash
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(FrmSplash));
            label1 = new AntdUI.Label();
            SuspendLayout();
            // 
            // label1
            // 
            label1.ForeColor = Color.Red;
            label1.Location = new Point(46, 219);
            label1.Name = "label1";
            label1.Size = new Size(412, 53);
            label1.TabIndex = 0;
            label1.Text = "系统初始化中...";
            // 
            // FrmSplash
            // 
            AutoScaleDimensions = new SizeF(14F, 31F);
            AutoScaleMode = AutoScaleMode.Font;
            BackgroundImage = Properties.Resources.seevision8;
            BackgroundImageLayout = ImageLayout.Stretch;
            ClientSize = new Size(500, 300);
            Controls.Add(label1);
            FormBorderStyle = FormBorderStyle.None;
            Icon = (Icon)resources.GetObject("$this.Icon");
            Name = "FrmSplash";
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Text = "FrmSplash";
            TopMost = true;
            ResumeLayout(false);
        }

        #endregion

        private AntdUI.Label label1;
    }
}