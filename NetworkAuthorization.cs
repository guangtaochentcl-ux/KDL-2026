using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace skdl_new_2025_test_tool
{
    public static class NetworkAuthorization
    {
        private static readonly Type NetFwPolicy2Type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", false);
        private static readonly Type NetFwRuleType = Type.GetTypeFromProgID("HNetCfg.FWRule", false);

        private const int NET_FW_IP_PROTOCOL_TCP = 6;
        private const int NET_FW_IP_PROTOCOL_UDP = 17;
        private const int NET_FW_ACTION_ALLOW = 1;
        private const int NET_FW_RULE_DIR_IN = 1;
        private const int NET_FW_RULE_DIR_OUT = 2;
        private const int NET_FW_RULE_DIR_IN_OUT = 3;
        private const int NET_FW_SCOPE_ALL = 0;

        public static void GrantNetworkAccess(string exePath, string appName)
        {
            if (string.IsNullOrWhiteSpace(exePath))
                throw new ArgumentNullException(nameof(exePath));
            if (string.IsNullOrWhiteSpace(appName))
                appName = System.IO.Path.GetFileNameWithoutExtension(exePath);

            try
            {
                dynamic fwPolicy2 = Activator.CreateInstance(NetFwPolicy2Type);
                if (fwPolicy2 == null)
                    throw new InvalidOperationException("Failed to create firewall policy instance.");

                RemoveExistingRules(fwPolicy2, exePath);
                AddRule(fwPolicy2, exePath, appName, NET_FW_RULE_DIR_IN);
                AddRule(fwPolicy2, exePath, appName, NET_FW_RULE_DIR_OUT);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to grant network access: {ex.Message}", ex);
            }
        }

        public static void GrantNetworkAccessBidirectional(string exePath, string appName)
        {
            if (string.IsNullOrWhiteSpace(exePath))
                throw new ArgumentNullException(nameof(exePath));
            if (string.IsNullOrWhiteSpace(appName))
                appName = System.IO.Path.GetFileNameWithoutExtension(exePath);

            try
            {
                dynamic fwPolicy2 = Activator.CreateInstance(NetFwPolicy2Type);
                if (fwPolicy2 == null)
                    throw new InvalidOperationException("Failed to create firewall policy instance.");

                RemoveExistingRules(fwPolicy2, exePath);
                AddRule(fwPolicy2, exePath, appName, NET_FW_RULE_DIR_IN_OUT);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to grant network access: {ex.Message}", ex);
            }
        }

        public static bool CheckNetworkAccess(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath))
                return false;

            try
            {
                dynamic fwPolicy2 = Activator.CreateInstance(NetFwPolicy2Type);
                if (fwPolicy2 == null)
                    return false;

                foreach (dynamic rule in fwPolicy2.Rules)
                {
                    if (rule.ApplicationName != null && 
                        rule.ApplicationName.Equals(exePath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (rule.Enabled && rule.Action == NET_FW_ACTION_ALLOW)
                            return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        public static void RemoveNetworkAccess(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath))
                throw new ArgumentNullException(nameof(exePath));

            try
            {
                dynamic fwPolicy2 = Activator.CreateInstance(NetFwPolicy2Type);
                if (fwPolicy2 == null)
                    throw new InvalidOperationException("Failed to create firewall policy instance.");

                RemoveExistingRules(fwPolicy2, exePath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to remove network access: {ex.Message}", ex);
            }
        }

        private static void RemoveExistingRules(dynamic fwPolicy2, string exePath)
        {
            try
            {
                var rulesToRemove = new System.Collections.Generic.List<dynamic>();
                foreach (dynamic rule in fwPolicy2.Rules)
                {
                    if (rule.ApplicationName != null && 
                        rule.ApplicationName.Equals(exePath, StringComparison.OrdinalIgnoreCase))
                    {
                        rulesToRemove.Add(rule);
                    }
                }
                foreach (var rule in rulesToRemove)
                {
                    fwPolicy2.Rules.Remove(rule);
                }
            }
            catch
            {
            }
        }
        public static void GrantNetworkAccessByNetsh(string exePath, string appName)
        {
            // 先删除旧规则
            var deleteProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall delete rule name=\"{appName}\"",
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            deleteProcess.Start();
            deleteProcess.WaitForExit();
            // 添加出站规则
            var outProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall add rule name=\"{appName}\" dir=out action=allow program=\"{exePath}\" enable=yes",
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            outProcess.Start();
            outProcess.WaitForExit();
            // 添加入站规则
            var inProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall add rule name=\"{appName}\" dir=in action=allow program=\"{exePath}\" enable=yes",
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            inProcess.Start();
            inProcess.WaitForExit();
        }
        private static void AddRule(dynamic fwPolicy2, string exePath, string appName, int direction)
        {
            dynamic rule = Activator.CreateInstance(NetFwRuleType);

            rule.Name = appName;
            rule.Description = $"Auto-authorized for {appName}";
            rule.ApplicationName = exePath;
            rule.Action = NET_FW_ACTION_ALLOW;
            rule.Direction = direction;
            rule.Enabled = true;
            rule.Protocol = 0;  // ANY protocol
            rule.InterfaceTypes = "All";
            // 移除所有端口相关设置 - 应用程序规则不需要这些
            fwPolicy2.Rules.Add(rule);
        }
    }
}