using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace skdl_new_2025_test_tool
{
    class PreviewSize
    {
        public int Width { get; set; }
        public int Height { get; set; }

        public PreviewSize(int w, int h)
        {
            this.Width = w;
            this.Height = h;
        }

        public string toString()
        {
            return string.Format("{0}x{1}", this.Width, this.Height);
        }
    }
}
