using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
//该文件主要定义了一个 CameraInfo 类，用于存储关于 UVC 相机设备的信息。
namespace VideoCapture_uvc
{
    public class CameraInfo
    {
        private Guid m_ClassID;//设备的类标识符

        private string m_Name; //设备的类名称

        private string m_DevicePath; //设备的路径

        private string m_InstanceId; //设备实例路径(唯一标识)

        public Guid ClassID
        {
            get
            {
                return m_ClassID;
            }

            set
            {
                m_ClassID = value;
            }
        }

        public string Name
        {
            get
            {
                return m_Name;
            }

            set
            {
                m_Name = value;
            }
        }

        public string DevicePath
        {
            get
            {
                return m_DevicePath;
            }

            set
            {
                m_DevicePath = value;
            }
        }

        public string InstanceId
        {
            get
            {
                return m_InstanceId;
            }

            set
            {
                m_InstanceId = value;
            }
        }

        public CameraInfo(Guid classID, string name, string devicePath, string instanceId = "")
        {
            m_ClassID = classID;
            m_Name = name;
            m_DevicePath = devicePath;
            m_InstanceId = instanceId ?? string.Empty;
        }
    }
}
