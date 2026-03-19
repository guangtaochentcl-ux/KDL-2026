using OpenQA.Selenium; // 引入 Selenium WebDriver 命名空间，用于控制浏览器
using OpenQA.Selenium.Edge; // 引入 Edge 浏览器驱动相关类
using OpenQA.Selenium.Support.UI; // 引入 WebDriverWait 等UI等待类
using System; // 引入系统基础类型
using System.Diagnostics; // 引入进程管理相关类，用于清理浏览器进程

namespace skdl_new_2025_test_tool // 定义命名空间
{
    // 视频检测类，用于检测网页视频是否正在播放
    public class VideoChecker
    {
        private IWebDriver? _driver; // Selenium WebDriver 实例，控制浏览器
        private EdgeDriverService? _driverService; // EdgeDriver 服务对象，管理驱动进程
        private string _url; // 视频页面的URL地址
        private string _loginUrl; // 登录页面的URL地址
        private string _username; // 登录用户名
        private string _password; // 登录密码

        // 构造函数，初始化所有必要参数
        public VideoChecker(string url, string loginUrl, string username, string password)
        {
            _url = url; // 设置视频URL
            _loginUrl = loginUrl; // 设置登录URL
            _username = username; // 设置用户名
            _password = password; // 设置密码
        }

        // 构造函数的重载版本，仅传入视频URL，用于不需要登录的情况
        public VideoChecker(string url)
        {
            _url = url; // 设置视频URL
            _loginUrl = ""; // 登录URL设为空
            _username = ""; // 用户名设为空
            _password = ""; // 密码设为空
        }

        // 初始化浏览器方法，创建并配置 Edge 浏览器实例
        public void Initialize()
        {
            // 创建 Edge 浏览器选项对象
            EdgeOptions options = new EdgeOptions();
            options.AddArguments("--start-maximized"); // 启动时最大化窗口
            options.AddArguments("--disable-extensions"); // 禁用浏览器扩展
            options.AddArgument("--no-sandbox"); // 禁用沙箱模式（Linux环境需要）
            options.AddArgument("--disable-dev-shm-usage"); // 禁用/dev/shm使用（避免内存问题）
            options.AddArgument("--disable-gpu"); // 禁用GPU硬件加速
            options.AddArgument("--remote-debugging-port=0"); // 设置远程调试端口
            options.AddArgument("--no-first-run"); // 跳过首次运行提示
            options.AddArgument("--no-default-browser-check"); // 跳过默认浏览器检查

            // 创建默认的 EdgeDriver 服务
            _driverService = EdgeDriverService.CreateDefaultService();
            _driverService.SuppressInitialDiagnosticInformation = true; // 抑制初始诊断信息输出
            _driverService.HideCommandPromptWindow = true; // 隐藏驱动命令行窗口

            // 使用配置创建 Edge 浏览器驱动实例
            _driver = new EdgeDriver(_driverService, options);
        }

        // 登录方法，模拟用户在登录页面输入账号密码并登录
        private void Login()
        {
            // 如果登录URL或用户名为空，则跳过登录流程
            if (string.IsNullOrEmpty(_loginUrl) || string.IsNullOrEmpty(_username))
            {
                return; // 直接返回，不执行登录
            }

            // 使用驱动导航到登录页面
            _driver!.Navigate().GoToUrl(_loginUrl);

            // 强制等待3秒，确保页面加载完成
            System.Threading.Thread.Sleep(3000);

            // 创建 WebDriverWait 等待对象，最长等待15秒
            WebDriverWait wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(15));

            // 查找用户名输入框（使用 XPath 定位：type为text且class包含form-control的input）
            var usernameInput = wait.Until(d => d.FindElement(By.XPath("//input[@type='text' and contains(@class, 'form-control')]")));
            usernameInput.SendKeys(_username); // 输入用户名

            // 查找密码输入框（使用 XPath 定位：type为password且class包含form-control的input）
            var passwordInput = wait.Until(d => d.FindElement(By.XPath("//input[@type='password' and contains(@class, 'form-control')]")));
            passwordInput.SendKeys(_password); // 输入密码

            // 查找并点击"记住我"复选框（查找class包含el-checkbox__label的span元素）
            var checkbox = wait.Until(d => d.FindElement(By.XPath("//span[contains(@class, 'el-checkbox__label')]")));
            checkbox.Click(); // 点击复选框

            // 查找登录按钮（通过id定位）
            var loginBtn = wait.Until(d => d.FindElement(By.Id("btn-login")));
            loginBtn.Click(); // 点击登录按钮

            // 等待1秒让登录完成
            System.Threading.Thread.Sleep(1000);
        }

        // 检测视频是否正在播放的主要方法
        public bool IsVideoPlaying(int timeoutSeconds = 10)
        {
            // 如果驱动未初始化，则先初始化浏览器
            if (_driver == null)
            {
                Initialize(); // 调用初始化方法
            }

            try
            {
                // 执行登录操作（如需要）
                Login();

                // 导航到视频页面
                _driver!.Navigate().GoToUrl(_url);

                // 创建等待对象，等待video标签出现
                WebDriverWait wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(timeoutSeconds));
                wait.Until(d => d.FindElements(By.TagName("video")).Count > 0); // 等待页面中存在video元素

                // 调用内部方法检查视频播放状态
                return CheckVideoPlaying();
            }
            catch (Exception ex)
            {
                // 捕获异常并输出错误信息
                Console.WriteLine($"检测失败: {ex.Message}");
                return false; // 返回false表示检测失败
            }
        }

        // 具体的视频播放检测逻辑实现
        private bool CheckVideoPlaying()
        {
            // 将驱动转换为JavaScript执行器，用于执行JS代码
            var js = (IJavaScriptExecutor)_driver;

            // 通过JS获取video元素的paused属性，判断是否暂停
            // 如果paused为true表示暂停，!paused为true表示未暂停（正在播放）
            bool notPaused = !(bool)js.ExecuteScript("return document.querySelector('video').paused");

            // 获取视频当前播放时间点（单位：秒）
            double t1 = Convert.ToDouble(js.ExecuteScript("return document.querySelector('video').currentTime"));
            
            // 等待1.5秒
            System.Threading.Thread.Sleep(1500);
            
            // 再次获取视频当前播放时间点
            double t2 = Convert.ToDouble(js.ExecuteScript("return document.querySelector('video').currentTime"));

            // 返回检测结果：必须同时满足"未暂停"且"时间在增长"才认为视频正在播放
            return notPaused && (t2 > t1);
        }

        // 清理进程方法，用于强制结束遗留的浏览器和驱动进程
        private static void CleanupProcesses()
        {
            try
            {
                // 查找并结束所有 msedgedriver 进程
                foreach (var p in Process.GetProcessesByName("msedgedriver"))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { } // 尝试结束进程，等待最多2秒
                }
                // 查找并结束所有 msedge 进程（Microsoft Edge浏览器主进程）
                foreach (var p in Process.GetProcessesByName("msedge"))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                }
                // 查找并结束所有 msedgewebview2 进程（Edge WebView2组件进程）
                foreach (var p in Process.GetProcessesByName("msedgewebview2"))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                }
            }
            catch { } // 忽略异常，继续执行
        }

        // 关闭浏览器并清理资源的方法
        public void Close()
        {
            try { _driver?.Quit(); } catch { } // 尝试关闭浏览器并结束驱动会话
            try { _driver?.Dispose(); } catch { } // 释放驱动资源
            _driver = null; // 将驱动置为空
            try { _driverService?.Dispose(); } catch { } // 释放驱动服务资源
            _driverService = null; // 将服务置为空
            CleanupProcesses(); // 清理遗留的浏览器进程
        }

        // 静态方法，提供便捷的检测接口，封装完整的检测流程
        public static bool Check(string url, string loginUrl = "", string username = "", string password = "", int timeoutSeconds = 10)
        {
            // 强制设置登录参数（测试用）- 这里硬编码了测试账号
            //loginUrl = "http://10.66.30.241/#/login";
            username = "admin"; // 强制使用admin用户名
            password = "admin"; // 强制使用admin密码
            
            // 输出日志信息
            Console.WriteLine("[VideoChecker] ========== 开始执行 ==========");
            Console.WriteLine($"[VideoChecker] 视频URL: {url}");
            Console.WriteLine($"[VideoChecker] 登录URL: '{loginUrl}'");
            Console.WriteLine($"[VideoChecker] 用户名: '{username}'");
            Console.WriteLine($"[VideoChecker] 密码: '{(string.IsNullOrEmpty(password) ? "空" : "有值")}'");

            // 创建 Edge 浏览器选项
            EdgeOptions options = new EdgeOptions();
            options.AddArguments("--start-maximized"); // 启动时最大化窗口
            options.AddArguments("--disable-extensions"); // 禁用扩展
            options.AddArgument("--no-sandbox"); // 禁用沙箱
            options.AddArgument("--disable-dev-shm-usage"); // 禁用/dev/shm使用

            // 创建 EdgeDriver 服务
            EdgeDriverService service = EdgeDriverService.CreateDefaultService();
            service.SuppressInitialDiagnosticInformation = true; // 抑制诊断信息
            service.HideCommandPromptWindow = true; // 隐藏命令行窗口

            // 创建浏览器驱动实例
            IWebDriver driver = new EdgeDriver(service, options);

            try
            {
                // 判断是否需要进行登录（loginUrl、username、password都不为空才需要登录）
                bool needLogin = !string.IsNullOrEmpty(loginUrl) && !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password);
                Console.WriteLine($"[VideoChecker] 需要登录: {needLogin}");

                // 如果需要登录，则执行登录流程
                if (needLogin)
                {
                    Console.WriteLine($"[VideoChecker] 访问登录页: {loginUrl}");
                    driver.Navigate().GoToUrl(loginUrl); // 导航到登录页面

                    System.Threading.Thread.Sleep(3000); // 等待3秒让页面加载

                    Console.WriteLine($"[VideoChecker] 当前URL: {driver.Url}");

                    // 创建等待对象，最长等待15秒
                    WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(15));

                    Console.WriteLine("[VideoChecker] 查找用户名输入框...");
                    // Ant Design 输入框 - 查找placeholder为"请输入用户名"的input元素
                    var usernameInput = wait.Until(d => d.FindElement(By.XPath("//input[@placeholder='请输入用户名']")));
                    usernameInput.SendKeys(username); // 输入用户名
                    Console.WriteLine($"[VideoChecker] 已输入用户名: {username}");

                    Console.WriteLine("[VideoChecker] 查找密码输入框...");
                    // 查找placeholder为"请输入密码"的input元素
                    var passwordInput = wait.Until(d => d.FindElement(By.XPath("//input[@placeholder='请输入密码']")));
                    passwordInput.SendKeys(password); // 输入密码
                    Console.WriteLine("[VideoChecker] 已输入密码");

                    Console.WriteLine("[VideoChecker] 点击登录按钮...");
                    // 查找登录按钮 - Ant Design框架，class包含ant-btn的button元素
                    var loginBtn = wait.Until(d => d.FindElement(By.XPath("//button[contains(@class, 'ant-btn')]")));
                    loginBtn.Click(); // 点击登录按钮

                    Console.WriteLine("[VideoChecker] 等待登录完成...");
                }

                System.Threading.Thread.Sleep(10000); // 等待10秒，确保视频加载完成

                // 创建等待对象，等待video标签出现在页面中
                WebDriverWait wait2 = new WebDriverWait(driver, TimeSpan.FromSeconds(timeoutSeconds));
                wait2.Until(d => d.FindElements(By.TagName("video")).Count > 0);

                // 通过JS执行器执行JavaScript代码
                var js = (IJavaScriptExecutor)driver;

                // 获取video元素的paused属性，判断是否暂停
                bool notPaused = !(bool)js.ExecuteScript("return document.querySelector('video').paused");

                // 获取视频当前播放时间
                double t1 = Convert.ToDouble(js.ExecuteScript("return document.querySelector('video').currentTime"));
                System.Threading.Thread.Sleep(1500); // 等待1.5秒
                // 返回检测结果：视频未暂停且时间在增长
                return notPaused && (Convert.ToDouble(js.ExecuteScript("return document.querySelector('video').currentTime")) > t1);
            }
            catch (Exception ex)
            {
                // 捕获异常并输出错误信息
                Console.WriteLine($"[VideoChecker] 检测失败: {ex.Message}");
                return false; // 返回false表示检测失败
            }
            finally
            {
                // finally块：无论成功还是失败都执行清理工作
                try { driver.Quit(); } catch { } // 关闭浏览器
                try { driver.Dispose(); } catch { } // 释放驱动资源
                try { service.Dispose(); } catch { } // 释放服务资源
                CleanupProcesses(); // 清理浏览器进程
                Console.WriteLine("[VideoChecker] ========== 执行结束 ==========");
            }
        }
    }
}


