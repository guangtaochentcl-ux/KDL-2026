using System.ComponentModel; // 必须引用，用于 INotifyPropertyChanged 支持

public class TestCases : INotifyPropertyChanged
{
    // 实现接口以便数据变化时UI自动刷新
    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private string _name;
    private string _description;
    private int _testCount;
    private string _testResult;
    private string _btnText = "开始测试"; // 默认按钮文字

    // 属性名 (Key) 将在 Column 中使用
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged("Name"); }
    }

    public string Description
    {
        get => _description;
        set { _description = value; OnPropertyChanged("Description"); }
    }

    public int TestCount
    {
        get => _testCount;
        set { _testCount = value; OnPropertyChanged("TestCount"); }
    }

    public string TestResult
    {
        get => _testResult;
        set { _testResult = value; OnPropertyChanged("TestResult"); }
    }

    // 这个属性专门用来显示“按钮”
    public string BtnText
    {
        get => _btnText;
        set { _btnText = value; OnPropertyChanged("BtnText"); }
    }
}