using System;
using System.Collections.Generic;
using System.Xml;

namespace LS.Futures.Shared
{
    /// <summary>
    /// 설정 로더 — LS.Engine 원본에서 복사(2026-07-03, 완전 독립 방침).
    /// 기존 LSStockTrading.exe.config(appSettings)에서 자격증명/서버/res경로 로드.
    /// </summary>
    public sealed class EngineConfig
    {
        public string ServerAddress;
        public int ServerPort = 20001;
        public string UserId, UserPwd, CertPwd;
        public string ResDir;
        public string AccountNo, AccountPwd;

        public static EngineConfig Load(string configPath, string defaultResDir)
        {
            var c = new Dictionary<string, string>();
            try
            {
                var doc = new XmlDocument();
                doc.Load(configPath);
                foreach (XmlNode n in doc.SelectNodes("//appSettings/add"))
                {
                    var k = n.Attributes?["key"]?.Value;
                    var v = n.Attributes?["value"]?.Value;
                    if (k != null) c[k] = v;
                }
            }
            catch (Exception ex) { Console.WriteLine("[config] 경고: " + ex.Message); }

            string G(string k, string d = "") => c.TryGetValue(k, out var v) && v != null ? v : d;
            return new EngineConfig
            {
                ServerAddress = G("ServerAddress"),
                ServerPort = int.Parse(G("ServerPort", "20001")),
                UserId = G("UserId"),
                UserPwd = G("UserPwd"),
                CertPwd = G("CertPwd"),
                ResDir = string.IsNullOrEmpty(G("DefaultResPath")) ? defaultResDir : G("DefaultResPath"),
                AccountNo = G("AccountNumber"),
                AccountPwd = G("AccountPassword")
            };
        }
    }
}
