using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace skdl_new_2025_test_tool
{
    public static class CModbusDll
    {
        public static byte[] WriteDO(int addr,int io,bool openclose)
        {
            byte[] src = new byte[8];
            src[0] = (byte)addr;
            src[1] = 0x05;
            src[2] = 0x00;
            src[3] = (byte)io;
            src[4] = (byte)((openclose) ? 0xff : 0x00);
            src[5] = 0x00;
            ushort crc = CMBRTU.CalculateCrc(src,6);
            src[6] = (byte)(crc & 0xff);
            src[7] = (byte)(crc>>8);
            return src;
        }
        public static byte[] WriteAllDO(int addr, int ionum, bool openclose)
        {
            byte[] src = new byte[10+(ionum-1)/8];
            int index = 0;
            src[index++] = (byte)addr;
            src[index++] = 0x0f;
            src[index++] = 0x00;
            src[index++] = 0x00;
            src[index++] = 0x00;
            src[index++] = (byte)ionum;
            src[index++] = (byte)((ionum+7)/8);
            src[index++] = (byte)((openclose) ? 0xff : 0x00);
            if (ionum>8) src[index++] = (byte)((openclose) ? 0xff : 0x00);
            if (ionum > 16) src[index++] = (byte)((openclose) ? 0xff : 0x00);
            if (ionum > 24) src[index++] = (byte)((openclose) ? 0xff : 0x00);
            ushort crc = CMBRTU.CalculateCrc(src, index);
            src[index++] = (byte)(crc & 0xff);
            src[index++] = (byte)(crc >> 8);
            return src;
        }
        public static byte[] ReadDO(int addr, int donum)
        {
            byte[] src = new byte[8];
            src[0] = (byte)addr;
            src[1] = 0x01;
            src[2] = 0x00;
            src[3] = 0x00;
            src[4] = 0x00;
            src[5] = (byte)donum;
            ushort crc = CMBRTU.CalculateCrc(src, 6);
            src[6] = (byte)(crc & 0xff);
            src[7] = (byte)(crc >> 8);
            return src;
        }
        public static byte[] ReadDI(int addr,int dinum )
        {
            byte[] src = new byte[8];
            src[0] = (byte)addr;
            src[1] = 0x02;
            src[2] = 0x00;
            src[3] = 0x00;
            src[4] = 0x00;
            src[5] = (byte)dinum;
            ushort crc = CMBRTU.CalculateCrc(src, 6);
            src[6] = (byte)(crc & 0xff);
            src[7] = (byte)(crc >> 8);
            return src;
        }

        public static byte[] ReadAIInfo(int addr, int regstart,int regnum)
        {
            byte[] src = new byte[8];
            src[0] = (byte)addr;
            src[1] = 0x04;
            src[2] = (byte)(regstart>>8);
            src[3] = (byte)(regstart & 0xff);
            src[4] = (byte)(regnum >> 8);
            src[5] = (byte)(regnum & 0xff);
            ushort crc = CMBRTU.CalculateCrc(src, 6);
            src[6] = (byte)(crc & 0xff);
            src[7] = (byte)(crc >> 8);
            return src;
        }

        public static byte[] WriteAOInfo(int addr, int regstart, short ao)
        {
            byte[] src = new byte[8];
            src[0] = (byte)addr;
            src[1] = 0x06;
            src[2] = (byte)(regstart >> 8);
            src[3] = (byte)(regstart & 0xff);
            src[4] = (byte)(ao >> 8);
            src[5] = (byte)(ao & 0xff);
            ushort crc = CMBRTU.CalculateCrc(src, 6);
            src[6] = (byte)(crc & 0xff);
            src[7] = (byte)(crc >> 8);
            return src;
        }

        public static byte[] WriteAOInfo(int addr, int regstart, int regnum, short[] ao)
        {
            int allnum = 6 + 1 + ao.Length * 2 + 2;
            byte[] src = new byte[allnum];
            src[0] = (byte)addr;
            src[1] = 0x10;
            src[2] = (byte)(regstart >> 8);
            src[3] = (byte)(regstart & 0xff);
            src[4] = (byte)(regnum >> 8);
            src[5] = (byte)(regnum & 0xff);
            src[6] = (byte)(ao.Length * 2);
            for (int i = 0; i < ao.Length; i++)
            {
                src[7 + 2 * i + 0] = (byte)(ao[i] >> 8);
                src[7 + 2 * i + 1] = (byte)(ao[i] & 0xff);
            }
            ushort crc = CMBRTU.CalculateCrc(src, allnum - 2);
            src[allnum - 2] = (byte)(crc & 0xff);
            src[allnum - 1] = (byte)(crc >> 8);
            return src;
        }
    }
}
