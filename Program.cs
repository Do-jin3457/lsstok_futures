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
            if (args.Contains("--idx-probe")) { RunIndexProbe(args); return; }

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
                System.Collections.Generic.Dictionary<string, double[]> byMode;
                if (restored.TryGetValue(pl.Cfg.Code, out byMode))
                {
                    double[] rr;
                    if (byMode.TryGetValue("V1", out rr)) pl.Signals.RestoreDay(rr[0], (int)rr[1], (int)rr[2], rr.Length > 3 ? rr[3] : 0);
                    // v5.24: TREND/TREND+OB도 V1과 동일하게 복원(그전엔 이 둘만 빠져있어 재시작마다 카드가 0으로 보이던 결함)
                    // v5.32: 수수료(sumExitPx 4번째 원소)도 함께 복원 — 재기동 후 원화 손익 과대표시 fix(회원 지적 7/11)
                    foreach (var kv in byMode)
                        if (kv.Key == "TREND" || kv.Key == "TREND+OB" || kv.Key == "V1-INV" || kv.Key == "V1.1" || kv.Key == "V1.2" || kv.Key == "BEST" || kv.Key == "BEST-V1" || kv.Key == "GAP" || kv.Key == "DFADE" || kv.Key == "DFADE-X" || kv.Key == "M30F" || kv.Key == "V1.1c" || kv.Key == "BBZ")
                            pl.Signals.RestoreTrendDay(kv.Key, kv.Value[0], (int)kv.Value[1], (int)kv.Value[2], kv.Value.Length > 3 ? kv.Value[3] : 0);
                }
                // v5.2: 대시보드 체결내역도 DB에서 재적재(재기동 후 빈 화면 fix)
                pl.Signals.RestoreFills(recorder.LoadRecentFills(pl.Cfg.Code, restoreDate, 30));
                // v5.25: 가격버퍼도 즉시 복원 — 안 하면 재시작마다 ER 33분 워밍업을 새로 거쳐 TREND류가 오래 멈춤
                pl.Signals.SeedMids(recorder.GetRecentMidSeries(pl.Cfg.Code, restoreDate, 36));
            };

            // 해외 심볼(분기물, --ovs로 교체 가능). ⚠️틱가치 원화환산=환율 1,400 가정(라벨), 수수료 편도 $1×왕복2(마이크로 e-mini 실측).
            // 2026-07-09 회원 지시: MES 제외(3일 연속 사실상 flat + 기초지수 SPI@SPX 데이터권한 부재) — 구독·신호·지수조회 전부 중지. 재개 시 --ovs MESU26,MNQU26.
            string ovsArg = GetStrArg(args, "--ovs", "MNQU26");
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
                    CommissionRt = 1.0 * 2 * 1400,   // 왕복 정액: 편도 $1 × 2 × 환율 1400 — 마이크로 e-mini 실측 수수료(2026-07-07 회원 확정, 종전 $7.5는 7.5배 과다였음)
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
                            // 미니 KOSPI200 근월물 자동 — t8435 MF에서 A05* 중 "만기 안 지난" 첫 항목. 미니는 매월 만기(롤오버 잦음).
                            // 🔴 2026-07-09 만기일 사고 fix: 마스터가 만기 당일 저녁까지 만기월물(2607)을 첫 항목으로 반환
                            //   (실측: 만기일 19:56 재기동서 죽은 2607 구독→야간 데이터 0). 만기 = 해당월 둘째 목요일 15:45 컷.
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
                                var alive = minis.FindAll(x => !MiniExpired(x.Hname, DateTime.Now));
                                var pick = alive.Count > 0 ? alive[0] : minis[0];   // 전멸 시 첫 항목 폴백(보수)
                                if (alive.Count == 0) Log("[boot] ⚠️ 미니 전월물 만기판정 — 마스터 첫 항목 폴백: " + minis[0].Hname);
                                else if (pick.Shcode != minis[0].Shcode) Log($"[boot] 미니 만기월물 스킵: {minis[0].Hname} → {pick.Hname} 선택 (만기=둘째목요일 15:45)");
                                addMini(pick.Shcode, pick.Hname);
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
            else
            {
                // XingAPI 세션 재접속 로직이 없고(Logout 이벤트도 공식 문서상 미발화, 실측으로도 불확실),
                // 세션 안에서 재로그인 시도는 COM 상태가 꼬이는 경우가 많다고 알려져 있어(2026-07-05 회원 확인) —
                // "끊김 감지 후 재접속" 대신 "매일 안전한 시각에 무조건 자체 종료 → 워치독(run_futures_loop.bat)이
                // 완전히 새 프로세스로 재기동"하는 예방적 방식 채택. 06:00~07:00은 국내야간(마감06:00)+CME(정비브레이크
                // 06:00~07:00) 둘 다 확실히 쉬는 시간이라 06:20으로 고정 — 05:50은 야간세션 마감(06:00) 전이라
                // 매일 10분치 데이터를 놓치게 되므로 부적합(최초 설계 오류, 세션 시간 재확인 후 수정).
                Console.WriteLine("종료하려면 Enter... (또는 매일 06:20 예방적 자동 재기동)");
                while (true)
                {
                    bool keyIn = false;
                    try { keyIn = Console.KeyAvailable; } catch { /* 입력 리다이렉트 환경에선 무시 */ }
                    if (keyIn) { Console.ReadLine(); break; }
                    var now = DateTime.Now;
                    if (now.Hour == 6 && now.Minute == 20 && now.Second < 10)
                    {
                        Log("[boot] 일일 예방적 재기동(06:20) — XingAPI 세션 갱신 목적, 워치독이 새 프로세스로 즉시 재기동");
                        break;
                    }
                    Thread.Sleep(2000);
                }
            }

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
        /// <summary>미니 월물 만기 판정(2026-07-09 fix) — Hname 끝 4자리 yymm 파싱, 만기=해당월 둘째 목요일 15:45.
        /// t8435 마스터가 만기 당일 저녁까지 만기월물을 첫 항목으로 주므로 지난 월물은 스킵해야 함.
        /// 형식 파싱 실패 시 false(생존 취급, 보수) — 기존 동작으로 폴백.</summary>
        private static bool MiniExpired(string hname, DateTime now)
        {
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(hname ?? "", @"(\d{2})(\d{2})\s*$");
                if (!m.Success) return false;
                int yy = int.Parse(m.Groups[1].Value), mm = int.Parse(m.Groups[2].Value);
                if (mm < 1 || mm > 12) return false;
                var first = new DateTime(2000 + yy, mm, 1);
                int offset = ((int)DayOfWeek.Thursday - (int)first.DayOfWeek + 7) % 7;
                var expiry = first.AddDays(offset + 7).AddHours(15).AddMinutes(45);   // 둘째 목요일 15:45
                return now > expiry;
            }
            catch { return false; }
        }

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
                        // gubun="예비(reserved)" 필드지만 실제 필터링 여부 미확인 — CME 포함 여부 다값 스캔(2026-07-05)
                        foreach (var g in new[] { "", "1", "2", "3", "4", "A", "B", "C", "U", "F" })
                        {
                            _rt.RequestOverseasMaster(g);
                            Thread.Sleep(2000);
                        }
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
            bool isOverseas = args.Contains("--overseas");
            bool isIdxHist = args.Contains("--idxhist");
            string shcode = GetStrArg(args, "--shcode", isIndex ? "101" : "A0169000");
            int minU = GetIntArg(args, "--min", 1);
            int pages = GetIntArg(args, "--pages", 3);
            string trLabel = isIndex ? "t8418 지수" : isIdxHist ? "t3518 해외지수" : isOverseas ? "o3103 해외선물" : "t8465 선물";
            Log($"[chart] {trLabel} 과거 분봉 조회: {shcode} {minU}분 x 최대{pages}페이지");

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
                        _rt.OnChartDone += delegate { done.Set(); };   // blind Sleep 대신 실제 완료/거부 이벤트로 신호(2026-07-05 fix)
                        if (isIndex) _rt.RequestIndexChart(shcode, minU, pages);
                        else if (isIdxHist) _rt.RequestIndexHistChart(shcode, minU, pages);
                        else if (isOverseas) _rt.RequestOverseasMinChart(shcode, minU, pages);
                        else _rt.RequestMinChart(shcode, minU, pages);
                        return;   // done.Set()은 OnChartDone 콜백에서만 — 여기서 바로 반환(Pump 스레드 안 막음)
                    }
                    done.Set();
                }
                catch (Exception ex) { Log("[chart] 오류: " + ex); done.Set(); }
            });
            done.Wait(1200000);   // 안전망(20분) — 정상적으론 OnChartDone이 훨씬 먼저 신호
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

        /// <summary>
        /// --idx-probe: t3518 해외지수(S&P500/나스닥100) 심볼 유효성 독립 확인. OVC 틱 없이도(주말 등) 즉시 조회.
        /// 사용: LS.Futures.exe --idx-probe --symbols "SPI@SPX,NAS@NDX"
        /// ⚠️ 0건이면 심볼무효/휴장 둘 다 가능 — 장중 재확인 필요(이 probe는 유효성 스크리닝용).
        /// </summary>
        private static void RunIndexProbe(string[] args)
        {
            string symArg = GetStrArg(args, "--symbols", "SPI@SPX,NAS@NDX");
            string[] syms = symArg.Split(',');
            Log($"[idxprobe] t3518 심볼 확인: {string.Join(", ", syms)}");

            var config = EngineConfig.Load(CfgPath, ResDir);
            ResPath.Configure(config.ResDir);

            var done = new ManualResetEventSlim();
            Pump.Run(delegate
            {
                try
                {
                    _session = new XingSessionService();
                    bool ok = _session.ConnectAndLogin(config);
                    Log(ok ? $"[idxprobe] 로그인 OK ({_session.LoginCode})" : $"[idxprobe] 로그인 실패 {_session.LoginCode} {_session.LoginMsg}");
                    if (ok)
                    {
                        _rt = new FuturesRealTime(Log);
                        var respSignal = new ManualResetEventSlim();
                        _rt.OnIndexProbe += delegate (string sym, bool okRes, string detail)
                        {
                            Log($"[idxprobe] {sym} → {(okRes ? "OK" : "FAIL")} : {detail}");
                            respSignal.Set();
                        };
                        // 전체 필드 덤프 × jgbn 전수(0=일/3=분/4=틱) — 어느 조합이 실제 지수레벨을 주는지 확인(2026-07-07)
                        string[] jgbns = GetStrArg(args, "--jgbns", "0,3,4").Split(',');
                        foreach (var s in syms)
                            foreach (var jg in jgbns)
                            {
                                respSignal.Reset();
                                _rt.ProbeIndexFull(s.Trim(), jg.Trim());
                                respSignal.Wait(5000);   // 응답 오면 즉시 다음(겹침 방지)
                                Thread.Sleep(400);        // TR 페이싱(초당 5건 제한 회피)
                            }
                    }
                    done.Set();
                }
                catch (Exception ex) { Log("[idxprobe] 오류: " + ex); done.Set(); }
            });
            done.Wait(30000);
            Thread.Sleep(1000);
            Pump.Invoke(delegate
            {
                try { if (_rt != null) _rt.Dispose(); } catch { }
                try { if (_session != null) _session.ReleaseAndDisconnect(); } catch { }
            });
            Pump.Stop();
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
