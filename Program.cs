using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LS.Futures.Shared;    // StaPump/XingSessionService/EngineConfig/ResPath — 원본에서 복사한 자체 소유본
using Microsoft.Owin.Hosting;

namespace LS.Futures
{
    /// <summary>
    /// KOSPI200 선물 전용 독립 헤드리스 엔진 (주식 엔진 LS.Engine과 별도 프로세스/포트).
    ///   역할: FC9/FH9 전량 수집 + 피처 스트리밍 + 신호 v0 가상매매(shadow) + 전용 원스크린 대시보드(:8801).
    ///   실주문 경로 없음 — 전부 가상 체결. 설계: docs/FUTURES_DESIGN_20260702.md (v2: 독립 프로그램).
    ///   사용: LS.Futures.exe [--port 8801] [--futcode A0169000] [--selftest]
    ///   ⚠️ winpc2 세션1(PsExec -i 1)에서 실행 — xingAPI COM 로그인 요건(6/29 교훈).
    /// </summary>
    internal static class Program
    {
        private const string CfgPath = @"C:\LSStockTrading_MVP\LSStockTrading\bin\Debug\LSStockTrading.exe.config";
        private const string ResDir = @"C:\LS_SEC\xingAPI\Res";
        private const string LogPath = @"C:\LSHeadless\futures_engine.log";

        private static readonly StaPump Pump = new StaPump();
        private static XingSessionService _session;
        private static FuturesRealTime _rt;

        [STAThread]
        private static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            // 예외 문자열 변환 자체가 실패해도(COM 예외 등) 최소 타입/시각은 남긴다 (7/3 새벽 무사인 크래시 교훈).
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                string detail;
                try { detail = e.ExceptionObject.ToString(); }
                catch
                {
                    try { detail = "(ToString 실패) type=" + e.ExceptionObject.GetType().FullName; }
                    catch { detail = "(예외 정보 추출 불가)"; }
                }
                Log("[FATAL] UnhandledException terminating=" + e.IsTerminating + " " + detail);
            };

            if (args.Contains("--probe-master")) { RunProbeMaster(); return; }
            if (args.Contains("--chart-probe")) { RunChartProbe(args); return; }

            int port = GetIntArg(args, "--port", 8801);
            string futcodeOverride = GetStrArg(args, "--futcode", "");
            bool selfTest = args.Contains("--selftest");
            string url = "http://+:" + port;

            Log($"[boot] LS.Futures 독립 엔진 시작 — port={port} futcode={(futcodeOverride == "" ? "(t9943 자동)" : futcodeOverride)}");

            var config = EngineConfig.Load(CfgPath, ResDir);
            ResPath.Configure(config.ResDir);   // res 미주입 = 전 TR 死 (6/29 사고 재발 방지)
            Log($"[boot] 서버 {config.ServerAddress}:{config.ServerPort} / 사용자 {config.UserId} / res {config.ResDir}");

            // 코어 조립 (v5 다중 종목: 미니200 + MES + MNQ)
            var recorder = new FuturesRecorder(Log);
            recorder.Start();
            FuturesApp.Recorder = recorder;
            recorder.OnSecond += delegate { foreach (var pl in FuturesApp.All()) pl.TickRates(); };
            // v5.1 재시작 정합: 당일 성적 복원 + 고아 ENTRY 마감 (회원 지적 — 재시작 시 가상 포지션 증발 부정합 fix)
            string restoreDate = FuturesRecorder.SessionTradeDate(DateTime.Now);   // v5.3: 야간 자정 넘김 시 전일 세션 성적까지 복원
            var restored = recorder.RestoreDay(restoreDate);
            Action<InstrumentPipeline> applyRestore = delegate (InstrumentPipeline pl)
            {
                double[] rr;
                if (restored.TryGetValue(pl.Cfg.Code, out rr))
                    pl.Signals.RestoreDay(rr[0], (int)rr[1], (int)rr[2]);
                // v5.2: 대시보드 체결내역도 DB에서 재적재(재기동 후 빈 화면 fix)
                pl.Signals.RestoreFills(recorder.LoadRecentFills(pl.Cfg.Code, restoreDate, 30));
            };

            // 해외 2종은 심볼 고정(분기물, --ovs로 교체 가능). ⚠️틱가치 원화환산=환율 1,400 가정(라벨), 수수료 $7.5×2 가정.
            string ovsArg = GetStrArg(args, "--ovs", "MESU26,MNQU26");
            var ovsSymbols = new System.Collections.Generic.List<string>();
            foreach (var symRaw in ovsArg.Split(','))
            {
                string sym = symRaw.Trim();
                if (sym == "") continue;
                ovsSymbols.Add(sym);
                bool isMnq = sym.StartsWith("MNQ");
                var cfg = new InstrumentConfig
                {
                    Code = sym,
                    Name = (isMnq ? "MNQ " : "MES ") + sym.Substring(3),
                    TickSize = 0.25,
                    TickValueKrw = isMnq ? 700 : 1750,
                    CommissionRt = 7.5 * 2 * 1400,   // 왕복 정액: 편도 $7.5 × 2 × 환율 1400(가정)
                    CommissionPct = 0,
                    Overseas = true
                };
                var pl = new InstrumentPipeline(cfg, Log);
                pl.Signals.OnFill += recorder.OnPaperFill;
                applyRestore(pl);
                FuturesApp.AddPipeline(pl);
            }

            // 내장 웹 (:8801) — urlacl 없으면 localhost 폴백
            IDisposable web = null;
            try { web = WebApp.Start<WebStartup>(url); Log($"[boot] 대시보드 {url}"); }
            catch (Exception ex)
            {
                Log($"[boot] {url} 바인딩 실패({ex.Message}) — localhost 폴백. tailscale 접속엔 urlacl 필요: netsh http add urlacl url=http://+:{port}/ user=Everyone");
                url = "http://localhost:" + port;
                web = WebApp.Start<WebStartup>(url);
                Log($"[boot] 대시보드 {url}");
            }

            // STA 펌프: COM 생성/로그인/구독 전부 이 스레드
            var ready = new ManualResetEventSlim();
            bool loginOk = false;
            Pump.Run(delegate
            {
                try
                {
                    _session = new XingSessionService();
                    loginOk = _session.ConnectAndLogin(config);
                    Log(loginOk ? $"[boot] 로그인 OK ({_session.LoginCode})" : $"[boot] 로그인 실패 code={_session.LoginCode} msg={_session.LoginMsg}");
                    FuturesApp.SetSession(loginOk, _session.LoginCode);

                    _rt = new FuturesRealTime(Log);
                    // 라우팅: 기록은 전량, 파이프라인(피처/신호)은 종목 정확 매칭 (STA 인라인 — O(1))
                    _rt.OnTick += delegate (LS.Futures.Models.FC9Data t)
                    {
                        recorder.OnTick(t);
                        var pl = FuturesApp.Exact(t.Futcode);
                        if (pl != null) pl.OnTick(t);
                    };
                    _rt.OnDepth += delegate (LS.Futures.Models.FH9Data d)
                    {
                        recorder.OnDepth(d);
                        var pl = FuturesApp.Exact(d.Futcode);
                        if (pl != null) pl.OnDepth(d);
                    };

                    // 미니 파이프라인 생성 헬퍼 (틱 0.02pt=1,000원 — 2026-07-03 실측)
                    Action<string, string> addMini = delegate (string code, string name)
                    {
                        var mcfg = new InstrumentConfig
                        {
                            Code = code, Name = name, TickSize = 0.02,
                            TickValueKrw = 1000, CommissionRt = 0, CommissionPct = 0.00003, Overseas = false   // 미니 0.003% 정률(주간·KRX야간 동일, 실측)
                        };
                        var mpl = new InstrumentPipeline(mcfg, Log);
                        mpl.Signals.OnFill += recorder.OnPaperFill;
                        applyRestore(mpl);
                        FuturesApp.AddPipeline(mpl);
                        _rt.Subscribe(code);
                    };

                    if (loginOk)
                    {
                        foreach (var sym in ovsSymbols) _rt.SubscribeOverseas(sym);   // MES/MNQ (시세 무료)

                        if (futcodeOverride != "")
                        {
                            addMini(futcodeOverride, "미니 " + futcodeOverride);
                        }
                        else
                        {
                            // 미니 KOSPI200 근월물 자동 — t8435 MF에서 A05* 첫 항목. 미니는 매월 만기(롤오버 잦음).
                            bool subscribed = false;
                            _rt.OnDerivMaster += delegate (System.Collections.Generic.List<LS.Futures.Models.T9943Item> items)
                            {
                                var minis = items.FindAll(x => x.Shcode != null && x.Shcode.StartsWith("A05"));
                                FuturesApp.SetMiniMonths(minis);
                                if (subscribed || minis.Count == 0)
                                {
                                    if (minis.Count == 0) Log("[boot] ⚠️ t8435 MF에 미니(A05*) 없음 — --futcode 지정 필요");
                                    return;
                                }
                                subscribed = true;
                                addMini(minis[0].Shcode, minis[0].Hname);
                            };
                            _rt.RequestDerivMaster("MF");
                        }
                    }
                    ready.Set();
                }
                catch (Exception ex)
                {
                    Log("[boot] STA 초기화 오류: " + ex);
                    ready.Set();
                }
            });

            if (!ready.Wait(40000)) Log("[boot] 경고: STA 초기화 40초 타임아웃");
            Log(loginOk ? "[boot] 가동 중 — 휴장 시 데이터 0 정상" : "[boot] 구조 부팅만 완료(로그인 실패)");

            if (selfTest) { Thread.Sleep(20000); Log("[boot] selftest 20초 경과 — 종료"); }
            else { Console.WriteLine("종료하려면 Enter..."); Console.ReadLine(); }

            // 종료: COM 해제는 생성 아파트먼트(STA)에서 (좀비 방지 — 주식 엔진과 동일 규율)
            try { if (web != null) web.Dispose(); } catch { }
            Pump.Invoke(delegate
            {
                try { if (_rt != null) _rt.Dispose(); } catch { }
                try { if (_session != null) _session.ReleaseAndDisconnect(); } catch { }
            });
            Pump.Stop();
            try { recorder.Dispose(); } catch { }
            HardExit();
        }

        /// <summary>
        /// --probe-master: t9943 마스터를 gubun 값별로 스캔 — 미니 KOSPI200 종목코드 확보용(휴장에도 동작).
        /// 미니 전용 실시간 res가 없어 FC9/FH9 통합 커버로 추정 — 그 전제의 futcode부터 확정한다.
        /// </summary>
        private static void RunProbeMaster()
        {
            Log("[probe] t9943 gubun 스캔 시작");
            var config = EngineConfig.Load(CfgPath, ResDir);
            ResPath.Configure(config.ResDir);
            var done = new ManualResetEventSlim();
            Pump.Run(delegate
            {
                try
                {
                    _session = new XingSessionService();
                    bool ok = _session.ConnectAndLogin(config);
                    Log(ok ? "[probe] 로그인 OK" : $"[probe] 로그인 실패 {_session.LoginCode} {_session.LoginMsg}");
                    if (ok)
                    {
                        _rt = new FuturesRealTime(Log);
                        // t9943 실측 결과: gubun 무관 정규 KOSPI200 선물만 → 미니는 t8435(파생종목마스터 MF)로 조회.
                        _rt.OnDerivMaster += delegate (System.Collections.Generic.List<LS.Futures.Models.T9943Item> items)
                        {
                            Log($"[probe] t8435 MF → {items.Count}건 (전체 덤프)");
                            foreach (var x in items)
                                Log($"[probe]   sh={x.Shcode} exp={x.Expcode} {x.Hname}");
                        };
                        _rt.RequestDerivMaster("MF");
                        Thread.Sleep(3000);
                        _rt.RequestOverseasMaster("");   // 해외선물 마스터 — MES/MNQ 심볼·증거금·거래시간
                        Thread.Sleep(6000);
                        // CME 심볼 규칙(U=9월, 26=2026) 직접 실측 — CME 개장 중이면 라이브 틱이 와야 함
                        _rt.ProbeOverseasSubscribe("MESU26");
                        _rt.ProbeOverseasSubscribe("MNQU26");
                        Thread.Sleep(15000);
                    }
                    done.Set();
                }
                catch (Exception ex) { Log("[probe] 오류: " + ex); done.Set(); }
            });
            done.Wait(60000);
            Thread.Sleep(2000);   // 마지막 응답 드레인
            Pump.Invoke(delegate
            {
                try { if (_rt != null) _rt.Dispose(); } catch { }
                try { if (_session != null) _session.ReleaseAndDisconnect(); } catch { }
            });
            Pump.Stop();
            Log("[probe] 종료");
            HardExit();
        }

        /// <summary>
        /// --chart-probe: t8465 선물 N분 과거 차트 조회(연속조회) → fut_minute 저장. 다장세 백테스트 데이터 확보.
        /// 사용: LS.Futures.exe --chart-probe --shcode A0169000 --min 1 --pages 5
        /// </summary>
        private static void RunChartProbe(string[] args)
        {
            bool isIndex = args.Contains("--index");
            string shcode = GetStrArg(args, "--shcode", isIndex ? "101" : "A0169000");
            int minU = GetIntArg(args, "--min", 1);
            int pages = GetIntArg(args, "--pages", 3);
            Log($"[chart] {(isIndex ? "t8418 지수" : "t8465 선물")} 과거 분봉 조회: {shcode} {minU}분 x 최대{pages}페이지");

            var config = EngineConfig.Load(CfgPath, ResDir);
            ResPath.Configure(config.ResDir);
            var recorder = new FuturesRecorder(Log);
            recorder.Start();

            var done = new ManualResetEventSlim();
            long total = 0;
            Pump.Run(delegate
            {
                try
                {
                    _session = new XingSessionService();
                    bool ok = _session.ConnectAndLogin(config);
                    Log(ok ? $"[chart] 로그인 OK ({_session.LoginCode})" : $"[chart] 로그인 실패 {_session.LoginCode} {_session.LoginMsg}");
                    if (ok)
                    {
                        _rt = new FuturesRealTime(Log);
                        _rt.OnMinChart += delegate (System.Collections.Generic.List<FutMinBar> bars)
                        {
                            total += bars.Count;
                            recorder.SaveMinBars(bars);
                        };
                        if (isIndex) _rt.RequestIndexChart(shcode, minU, pages);
                        else _rt.RequestMinChart(shcode, minU, pages);
                        Thread.Sleep(1500 * pages + 8000);   // 연속조회(페이싱 300ms) 완료 여유 대기
                    }
                    done.Set();
                }
                catch (Exception ex) { Log("[chart] 오류: " + ex); done.Set(); }
            });
            done.Wait(120000);
            Thread.Sleep(2000);
            Log($"[chart] 총 {total}봉 저장 완료 (db={recorder.DbPath})");
            Pump.Invoke(delegate
            {
                try { if (_rt != null) _rt.Dispose(); } catch { }
                try { if (_session != null) _session.ReleaseAndDisconnect(); } catch { }
            });
            Pump.Stop();
            try { recorder.Dispose(); } catch { }
            HardExit();
        }

        /// <summary>커널 즉시 종료 — COM RCW finalizer 데드락(좀비) 방지. LS.Engine과 동일 패턴.</summary>
        private static void HardExit()
        {
            try { Console.Out.Flush(); } catch { }
            try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { }
            Environment.Exit(0);
        }

        private static void Log(string m)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + m;
            Console.WriteLine(line);
            FuturesApp.PushLog(line);
            try { File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        private static string GetStrArg(string[] args, string name, string dflt)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == name && i + 1 < args.Length && !args[i + 1].StartsWith("--")) return args[i + 1];
                if (args[i].StartsWith(name + "=")) return args[i].Substring(name.Length + 1);
            }
            return dflt;
        }

        private static int GetIntArg(string[] args, string name, int dflt)
        {
            string s = GetStrArg(args, name, "");
            int v;
            return int.TryParse(s, out v) ? v : dflt;
        }
    }
}
