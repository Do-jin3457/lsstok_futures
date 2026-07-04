using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using XA_SESSIONLib;
using XA_DATASETLib;

namespace LS.Futures.Shared
{
    /// <summary>
    /// XingAPI 세션 서비스 — LS.Engine 원본에서 복사(2026-07-03, 완전 독립 방침).
    /// ⚠️ 반드시 STA 스레드(StaPump) 위에서 생성/호출 — COM 어피니티 필수.
    /// 로그인은 config 자격증명(인증서 자동) → ConnectServer + Login + 콜백 대기.
    /// </summary>
    public sealed class XingSessionService
    {
        private XASession _session;
        private volatile bool _loginDone;
        public string LoginCode = "", LoginMsg = "";
        public bool LoggedIn { get; private set; }

        public bool ConnectFailed { get; private set; }

        public bool ConnectAndLogin(EngineConfig cfg, int timeoutMs = 20000)
        {
            ConnectFailed = false;
            _session = new XASession();
            ((_IXASessionEvents_Event)_session).Login += (c, m) => { LoginCode = c; LoginMsg = m; _loginDone = true; };
            ((_IXASessionEvents_Event)_session).Logout += () => Console.WriteLine("[session] Logout 이벤트 수신");

            if (!_session.ConnectServer(cfg.ServerAddress, cfg.ServerPort))
            {
                int ec = 0; try { ec = _session.GetLastError(); } catch { }
                Console.WriteLine($"[session] ConnectServer 실패 err={ec} (winpc2 세션1 콘솔에서 실행했는지 확인)");
                ConnectFailed = true;
                return false;
            }
            _session.Login(cfg.UserId, cfg.UserPwd, cfg.CertPwd, 0, false);
            Pump(() => _loginDone, timeoutMs);
            LoggedIn = _loginDone && LoginCode == "0000";
            return LoggedIn;
        }

        public void Disconnect() { try { _session?.DisconnectServer(); } catch { } }

        /// <summary>COM 명시 해제+연결 종료 — 종료 직전 STA 스레드에서 호출(CLR 종료 시 COM Finalizer 크래시 방지).</summary>
        public void ReleaseAndDisconnect()
        {
            try { _session?.Logout(); } catch { }
            try { _session?.DisconnectServer(); } catch { }
            if (_session != null)
            {
                try
                {
                    int remaining = System.Runtime.InteropServices.Marshal.ReleaseComObject(_session);
                    while (remaining > 0)
                        remaining = System.Runtime.InteropServices.Marshal.ReleaseComObject(_session);
                }
                catch { }
                _session = null;
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        /// <summary>STA 펌프 — 콜백(COM 이벤트) 도착까지 메시지 처리.</summary>
        private static void Pump(Func<bool> until, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (!until() && sw.ElapsedMilliseconds < timeoutMs) { Application.DoEvents(); Thread.Sleep(5); }
        }
    }
}
