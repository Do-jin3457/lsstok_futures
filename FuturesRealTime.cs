using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LS.Futures.Models;
using LS.Futures.Shared;
using XA_DATASETLib;

namespace LS.Futures
{
    /// <summary>
    /// KOSPI200 선물 전용 슬림 Xing 레이어 — FC9(체결)/FH9(호가) 실시간 + t9943(마스터) 조회.
    /// 주식 엔진의 XingRealTimeService에서 선물에 필요한 최소만 독립 구현(설계 재작성 v2 — 독립 프로그램).
    /// ⚠️ 반드시 STA 펌프 스레드에서 생성/구독(COM 어피니티). res 경로는 AppSettings.ConfigureResPath 선행 필수.
    /// 수신 즉시 Stopwatch 단조시계 스탬프(감쇠곡선 x축, 설계 D6).
    /// </summary>
    public sealed class FuturesRealTime : IDisposable
    {
        private readonly Action<string> _log;
        private XAReal _fc9;
        private XAReal _fh9;
        private XAReal _dc0;   // KRX 야간파생 체결 (필드 = FC9 동일 실측 2026-07-03)
        private XAReal _dh0;   // KRX 야간파생 호가 (필드 = FH9 동일)
        private XAQuery _t9943;
        private XAQuery _t8435;
        private XAQuery _t8465;   // 선물/옵션 N분 차트(과거조회) — 다장세 백테스트용 (2026-07-04)
        private XAQuery _t8418;   // 업종(지수) N분 차트 — KOSPI200 지수 과거(만기무관 몇년), R7 시간대모멘텀·괴리율용
        private XAQuery _t3518;   // 해외 기초지수(S&P500/나스닥100) — 해외 basis 계산용 (2026-07-04, 30초 스로틀 조회)
        // 해외선물 → 기초지수 심볼 (t3518 kind=S). NDX는 추정 — 월요일 US장 검증.
        private readonly System.Collections.Generic.Dictionary<string, string> _futToIdx = new System.Collections.Generic.Dictionary<string, string>
        {
            { "MESU26", "SPI@SPX" },   // MES 기초 = S&P 500
            { "MNQU26", "NAS@NDX" },   // MNQ 기초 = 나스닥100(추정)
        };
        private readonly System.Collections.Generic.Dictionary<string, double> _idxPrice = new System.Collections.Generic.Dictionary<string, double>();
        private readonly System.Collections.Generic.Dictionary<string, long> _lastIdxReqMs = new System.Collections.Generic.Dictionary<string, long>();
        private string _lastIdxSym = "";
        private bool _disposed;

        public event Action<FC9Data> OnTick;
        public event Action<FH9Data> OnDepth;
        public event Action<List<T9943Item>> OnMaster;
        public event Action<List<T9943Item>> OnDerivMaster;   // t8435 (미니 포함 파생 전체 — 동일 필드라 T9943Item 재사용)
        public event Action<List<FutMinBar>> OnMinChart;      // t8465 분봉 배치(연속조회 페이지 단위)

        public FuturesRealTime(Action<string> log)
        {
            _log = log ?? (_ => { });
            _fc9 = new XAReal();
            _fc9.ReceiveRealData += delegate (string tr) { if (tr == "FC9") ParseTick(_fc9); };
            _fh9 = new XAReal();
            _fh9.ReceiveRealData += delegate (string tr) { if (tr == "FH9") ParseDepth(_fh9); };
            _dc0 = new XAReal();
            _dc0.ReceiveRealData += delegate (string tr) { if (tr == "DC0") ParseTick(_dc0); };
            _dh0 = new XAReal();
            _dh0.ReceiveRealData += delegate (string tr) { if (tr == "DH0") ParseDepth(_dh0); };
            _t9943 = new XAQuery();
            _t9943.ReceiveData += OnRecv_t9943;
            _t9943.ReceiveMessage += (isErr, code, msg) => { if (isErr) _log($"[t9943] 서버 메시지 err code={code} {msg}"); };
            _t8435 = new XAQuery();
            _t8435.ReceiveData += OnRecv_t8435;
            _t8435.ReceiveMessage += (isErr, code, msg) => { if (isErr) _log($"[t8435] 서버 메시지 err code={code} {msg}"); };
            _t8465 = new XAQuery();
            _t8465.ReceiveData += OnRecv_t8465;
            _t8465.ReceiveMessage += (isErr, code, msg) => _log($"[t8465] 서버메시지 err={isErr} code={code} {msg}");
            _t8418 = new XAQuery();
            _t8418.ReceiveData += OnRecv_t8418;
            _t8418.ReceiveMessage += (isErr, code, msg) => _log($"[t8418] 서버메시지 err={isErr} code={code} {msg}");
            _t3518 = new XAQuery();
            _t3518.ReceiveData += OnRecv_t3518;
            _t3518.ReceiveMessage += (isErr, code, msg) => _log($"[t3518] 서버메시지 err={isErr} code={code} {msg}");
        }

        /// <summary>지수(업종) 과거 분봉 조회 시작 — upcode=업종코드(KOSPI200="101"). 만기 없어 과거 몇 년 가능.</summary>
        public void RequestIndexChart(string upcode, int minUnit, int maxPages)
        {
            _chartCode = upcode; _chartMin = minUnit;
            _chartPages = 0; _chartMaxPages = maxPages;
            SendIndexReq("", "");
        }

        private void SendIndexReq(string ctsDate, string ctsTime)
        {
            try
            {
                // V2(01_V2_ALL) 검증 레시피: TR t8409(업종차트)+t8409InBlock+comp_yn=Y/qrycnt=2000/sdate 빈값.
                // t8418은 서버가 미인식(Request rc=-23="TR정보 없음") — 2026-07-04 실측. t8409로 전환.
                _t8418.LoadFromResFile(ResPath.Get("t8409.res"));
                _t8418.SetFieldData("t8409InBlock", "shcode", 0, _chartCode);
                _t8418.SetFieldData("t8409InBlock", "ncnt", 0, _chartMin.ToString());
                _t8418.SetFieldData("t8409InBlock", "qrycnt", 0, "2000");
                _t8418.SetFieldData("t8409InBlock", "nday", 0, "0");
                _t8418.SetFieldData("t8409InBlock", "sdate", 0, "");
                _t8418.SetFieldData("t8409InBlock", "edate", 0, DateTime.Now.ToString("yyyyMMdd"));  // t8465 교훈: edate 비면 0봉
                _t8418.SetFieldData("t8409InBlock", "comp_yn", 0, "Y");
                _t8418.SetFieldData("t8409InBlock", "cts_date", 0, ctsDate);
                _t8418.SetFieldData("t8409InBlock", "cts_time", 0, ctsTime);
                int rc = _t8418.Request(ctsDate != "" || ctsTime != "");
                if (rc < 0) _log($"[t8418] Request 거부 rc={rc} (shcode={_chartCode}) — 에러코드 확인 필요");
            }
            catch (Exception ex) { _log("[t8418] 요청 실패: " + ex.Message); }
        }

        private void OnRecv_t8418(string szTrCode)
        {
            if (szTrCode != "t8409") return;
            try
            {
                int cnt = _t8418.GetBlockCount("t8409OutBlock1");
                var bars = new List<FutMinBar>(cnt);
                for (int i = 0; i < cnt; i++)
                {
                    bars.Add(new FutMinBar
                    {
                        Code = "IDX" + _chartCode,   // 지수 구분 접두어
                        Date = Trimmed(_t8418.GetFieldData("t8409OutBlock1", "date", i)),
                        Time = Trimmed(_t8418.GetFieldData("t8409OutBlock1", "time", i)),
                        Open = Dec(_t8418.GetFieldData("t8409OutBlock1", "open", i)),
                        High = Dec(_t8418.GetFieldData("t8409OutBlock1", "high", i)),
                        Low = Dec(_t8418.GetFieldData("t8409OutBlock1", "low", i)),
                        Close = Dec(_t8418.GetFieldData("t8409OutBlock1", "close", i)),
                        Volume = Lng(_t8418.GetFieldData("t8409OutBlock1", "jdiff_vol", i)),
                        Value = Lng(_t8418.GetFieldData("t8409OutBlock1", "value", i)),
                        OpenYak = 0
                    });
                }
                string ctsD = Trimmed(_t8418.GetFieldData("t8409OutBlock", "cts_date", 0));
                string ctsT = Trimmed(_t8418.GetFieldData("t8409OutBlock", "cts_time", 0));
                _chartPages++;
                string firstDT = cnt > 0 ? bars[0].Date + " " + bars[0].Time : "-";
                string lastDT = cnt > 0 ? bars[cnt - 1].Date + " " + bars[cnt - 1].Time : "-";
                _log($"[t8418] p{_chartPages} {cnt}봉 [{firstDT} ~ {lastDT}] cts=({ctsD},{ctsT})");
                var h = OnMinChart; if (h != null) h(bars);

                bool more = (ctsD != "" || ctsT != "") && cnt > 0 && _chartPages < _chartMaxPages;
                if (more) { System.Threading.Thread.Sleep(300); SendIndexReq(ctsD, ctsT); }
                else _log($"[t8418] 조회 완료 — 총 {_chartPages}페이지");
            }
            catch (Exception ex) { _log("[t8418] 파싱 오류: " + ex.Message); }
        }

        /// <summary>해외 기초지수(S&P500/나스닥100) 현재가 조회 — ParseOvsTick(STA 콜백)에서 30초 스로틀 호출.
        /// 결과는 _idxPrice에 저장 → ParseOvsTick이 basis=선물−지수 주입. ⚠️미검증(월요일 US장 확인).</summary>
        private void QueryIndex(string symbol)
        {
            try
            {
                _t3518.LoadFromResFile(ResPath.Get("t3518.res"));
                _t3518.SetFieldData("t3518InBlock", "kind", 0, "S");        // S=해외지수
                _t3518.SetFieldData("t3518InBlock", "symbol", 0, symbol);
                _t3518.SetFieldData("t3518InBlock", "cnt", 0, "1");
                _t3518.SetFieldData("t3518InBlock", "jgbn", 0, "0");
                _t3518.SetFieldData("t3518InBlock", "nmin", 0, "1");
                _lastIdxSym = symbol;
                int rc = _t3518.Request(false);
                if (rc < 0) _log($"[t3518] {symbol} Request 거부 rc={rc}");
            }
            catch (Exception ex) { _log("[t3518] 요청 실패: " + ex.Message); }
        }

        private void OnRecv_t3518(string szTrCode)
        {
            if (szTrCode != "t3518") return;
            try
            {
                int cnt = _t3518.GetBlockCount("t3518OutBlock1");
                if (cnt <= 0) { _log($"[t3518] {_lastIdxSym} 0건(심볼/파라미터 확인 필요)"); return; }
                double px;
                if (double.TryParse(Trimmed(_t3518.GetFieldData("t3518OutBlock1", "price", cnt - 1)), out px) && px > 0)
                {
                    _idxPrice[_lastIdxSym] = px;
                    _log($"[t3518] {_lastIdxSym} 기초지수={px:F2}");
                }
            }
            catch (Exception ex) { _log("[t3518] 파싱 오류: " + ex.Message); }
        }

        // ── t8465 선물/옵션 N분 과거 차트 (연속조회로 다장세 수집) ──
        private string _chartCode;
        private int _chartMin;
        private int _chartPages, _chartMaxPages;

        /// <summary>과거 분봉 조회 시작. minUnit=분단위(1/3/5…), maxPages=연속조회 반복 상한(페이지당 최대 2000봉).</summary>
        public void RequestMinChart(string shcode, int minUnit, int maxPages)
        {
            _chartCode = shcode; _chartMin = minUnit;
            _chartPages = 0; _chartMaxPages = maxPages;
            SendChartReq("", "");
        }

        private void SendChartReq(string ctsDate, string ctsTime)
        {
            try
            {
                _t8465.LoadFromResFile(ResPath.Get("t8465.res"));
                _t8465.SetFieldData("t8465InBlock", "shcode", 0, _chartCode);
                _t8465.SetFieldData("t8465InBlock", "ncnt", 0, _chartMin.ToString());
                _t8465.SetFieldData("t8465InBlock", "qrycnt", 0, "500");
                _t8465.SetFieldData("t8465InBlock", "nday", 0, "0");
                _t8465.SetFieldData("t8465InBlock", "sdate", 0, "20260101");             // 조회 시작(넉넉히 과거)
                _t8465.SetFieldData("t8465InBlock", "edate", 0, DateTime.Now.ToString("yyyyMMdd"));  // 종료=오늘
                _t8465.SetFieldData("t8465InBlock", "comp_yn", 0, "N");   // 비압축(500봉/페이지) — 압축은 별도 디코딩 필요
                _t8465.SetFieldData("t8465InBlock", "cts_date", 0, ctsDate);
                _t8465.SetFieldData("t8465InBlock", "cts_time", 0, ctsTime);
                bool cont = ctsDate != "" || ctsTime != "";
                _t8465.Request(cont);
            }
            catch (Exception ex) { _log("[t8465] 요청 실패: " + ex.Message); }
        }

        private void OnRecv_t8465(string szTrCode)
        {
            if (szTrCode != "t8465") return;
            try
            {
                int cnt = _t8465.GetBlockCount("t8465OutBlock1");
                var bars = new List<FutMinBar>(cnt);
                for (int i = 0; i < cnt; i++)
                {
                    bars.Add(new FutMinBar
                    {
                        Code = _chartCode,
                        Date = Trimmed(_t8465.GetFieldData("t8465OutBlock1", "date", i)),
                        Time = Trimmed(_t8465.GetFieldData("t8465OutBlock1", "time", i)),
                        Open = Dec(_t8465.GetFieldData("t8465OutBlock1", "open", i)),
                        High = Dec(_t8465.GetFieldData("t8465OutBlock1", "high", i)),
                        Low = Dec(_t8465.GetFieldData("t8465OutBlock1", "low", i)),
                        Close = Dec(_t8465.GetFieldData("t8465OutBlock1", "close", i)),
                        Volume = Lng(_t8465.GetFieldData("t8465OutBlock1", "jdiff_vol", i)),
                        Value = Lng(_t8465.GetFieldData("t8465OutBlock1", "value", i)),
                        OpenYak = Lng(_t8465.GetFieldData("t8465OutBlock1", "openyak", i))
                    });
                }
                string ctsD = Trimmed(_t8465.GetFieldData("t8465OutBlock", "cts_date", 0));
                string ctsT = Trimmed(_t8465.GetFieldData("t8465OutBlock", "cts_time", 0));
                _chartPages++;
                string firstDT = cnt > 0 ? bars[0].Date + " " + bars[0].Time : "-";
                string lastDT = cnt > 0 ? bars[cnt - 1].Date + " " + bars[cnt - 1].Time : "-";
                _log($"[t8465] p{_chartPages} {cnt}봉 [{firstDT} ~ {lastDT}] cts=({ctsD},{ctsT})");
                var h = OnMinChart; if (h != null) h(bars);

                bool more = (ctsD != "" || ctsT != "") && cnt > 0 && _chartPages < _chartMaxPages;
                if (more) { System.Threading.Thread.Sleep(300); SendChartReq(ctsD, ctsT); }   // 초당 요청제한 회피(연속조회 페이싱)
                else _log($"[t8465] 조회 완료 — 총 {_chartPages}페이지");
            }
            catch (Exception ex) { _log("[t8465] 파싱 오류: " + ex.Message); }
        }

        /// <summary>해외선물 마스터 조회 (o3101) — MES/MNQ 심볼·틱단위·증거금·거래시간 확보용(2026-07-04 약정 후 res 개방).</summary>
        public void RequestOverseasMaster(string gubun = "")
        {
            try
            {
                var q = new XAQuery();
                q.ReceiveData += delegate (string tr)
                {
                    if (tr != "o3101") return;
                    try
                    {
                        int n = q.GetBlockCount("o3101OutBlock");
                        _log($"[o3101] 해외선물 마스터 {n}건");
                        for (int i = 0; i < n; i++)
                        {
                            string sym = q.GetFieldData("o3101OutBlock", "Symbol", i);
                            string nm = q.GetFieldData("o3101OutBlock", "SymbolNm", i);
                            string exch = q.GetFieldData("o3101OutBlock", "ExchCd", i);
                            string unt = q.GetFieldData("o3101OutBlock", "UntPrc", i);
                            string ctr = q.GetFieldData("o3101OutBlock", "CtrtPrAmt", i);
                            string mgn = q.GetFieldData("o3101OutBlock", "OpngMgn", i);
                            string st = q.GetFieldData("o3101OutBlock", "DlStrtTm", i);
                            string et = q.GetFieldData("o3101OutBlock", "DlEndTm", i);
                            _log($"[o3101]   {sym} | {nm} | {exch} | 틱{unt} 계약{ctr} 증거금{mgn} | {st}~{et}");
                        }
                    }
                    catch (Exception ex) { _log("[o3101] 파싱 오류: " + ex.Message); }
                };
                q.ReceiveMessage += (isErr, code, msg) => { if (isErr) _log($"[o3101] err {code} {msg}"); };
                q.LoadFromResFile(ResPath.Get("o3101.res"));
                q.SetFieldData("o3101InBlock", "gubun", 0, gubun);
                q.Request(false);
            }
            catch (Exception ex) { _log("[o3101] 조회 실패: " + ex.Message); }
        }

        // ── 해외선물 정식 구독 (OVC 체결/OVH 호가 — 2026-07-04 약정 개통, MESU26 라이브 실증) ──
        private readonly List<XAReal> _ovsReals = new List<XAReal>();

        public void SubscribeOverseas(string symbol)
        {
            try
            {
                var c = new XAReal();
                c.ReceiveRealData += delegate (string tr) { if (tr == "OVC") ParseOvsTick(c); };
                c.ResFileName = ResPath.Get("OVC.res");
                c.SetFieldData("InBlock", "symbol", symbol);
                c.AdviseRealData();
                _ovsReals.Add(c);

                var h = new XAReal();
                h.ReceiveRealData += delegate (string tr) { if (tr == "OVH") ParseOvsDepth(h); };
                h.ResFileName = ResPath.Get("OVH.res");
                h.SetFieldData("InBlock", "symbol", symbol);
                h.AdviseRealData();
                _ovsReals.Add(h);
                _log($"[xing] 해외 OVC/OVH 구독 시작: {symbol}");
            }
            catch (Exception ex) { _log($"[xing] 해외 구독 실패 {symbol}: " + ex.Message); }
        }

        /// <summary>OVC → FC9Data 매핑 (베이시스류는 해외 미제공=0). CheTime=한국시각(kortm).</summary>
        private void ParseOvsTick(XAReal r)
        {
            long recvTicks = Stopwatch.GetTimestamp();
            var recvAt = DateTime.Now;
            try
            {
                var d = new FC9Data();
                d.Futcode = Trimmed(r.GetFieldData("OutBlock", "symbol"));
                d.CheTime = r.GetFieldData("OutBlock", "kortm");
                d.Price = Dec(r.GetFieldData("OutBlock", "curpr"));
                string sign = Trimmed(r.GetFieldData("OutBlock", "ydiffSign"));
                decimal chg = Dec(r.GetFieldData("OutBlock", "ydiffpr"));
                d.Change = (sign == "4" || sign == "5" || sign == "-") ? -chg : chg;
                d.Drate = Dec(r.GetFieldData("OutBlock", "chgrate"));
                d.Open = Dec(r.GetFieldData("OutBlock", "open"));
                d.High = Dec(r.GetFieldData("OutBlock", "high"));
                d.Low = Dec(r.GetFieldData("OutBlock", "low"));
                d.CGubun = Trimmed(r.GetFieldData("OutBlock", "cgubun"));
                d.CVolume = Lng(r.GetFieldData("OutBlock", "trdq"));
                d.Volume = Lng(r.GetFieldData("OutBlock", "totq"));
                d.MdVolume = Lng(r.GetFieldData("OutBlock", "mdvolume"));
                d.MsVolume = Lng(r.GetFieldData("OutBlock", "msvolume"));
                d.ReceivedAt = recvAt;
                d.RecvTicks = recvTicks;
                // 해외 basis: 기초지수 대비 괴리(포인트) 주입 + 30초 스로틀 조회. STA 콜백 스레드라 Request 안전.
                // ⚠️ 미검증(월요일 US장 확인 전) — 값 수집·로깅만, SignalEngine 신호엔 아직 미반영(cBasis=0 유지).
                string idxSym;
                if (_futToIdx.TryGetValue(d.Futcode, out idxSym))
                {
                    double ip;
                    if (_idxPrice.TryGetValue(idxSym, out ip) && ip > 0)
                        d.Kasis = d.Price - (decimal)ip;   // basis = 선물 − 기초지수
                    long nowMs = recvTicks / (Stopwatch.Frequency / 1000);
                    long last; _lastIdxReqMs.TryGetValue(idxSym, out last);
                    if (nowMs - last > 30000) { _lastIdxReqMs[idxSym] = nowMs; QueryIndex(idxSym); }
                }
                var h = OnTick; if (h != null) h(d);
            }
            catch (Exception ex) { _log("[OVC] 파싱 오류: " + ex.Message); }
        }

        /// <summary>OVH → FH9Data 매핑 (offerno/bidno → 건수 슬롯).</summary>
        private void ParseOvsDepth(XAReal r)
        {
            long recvTicks = Stopwatch.GetTimestamp();
            var recvAt = DateTime.Now;
            try
            {
                var d = new FH9Data();
                d.Futcode = Trimmed(r.GetFieldData("OutBlock", "symbol"));
                // OVH엔 한국시각 필드(kortm) 없이 현지시각 hotime만 있음(=ET, 대시보드에 11:33 식으로 오표시).
                // 국내 FH9(한국시각 hotime)와 형식 통일 위해 한국 수신시각으로 채움(v5.3, 회원 지적 7/4). 원본은 recv_at 컬럼에 보존.
                d.HoTime = recvAt.ToString("HHmmss");
                d.TotOfferRem = Lng(r.GetFieldData("OutBlock", "totofferrem"));
                d.TotBidRem = Lng(r.GetFieldData("OutBlock", "totbidrem"));
                d.TotOfferCnt = Lng(r.GetFieldData("OutBlock", "totoffercnt"));
                d.TotBidCnt = Lng(r.GetFieldData("OutBlock", "totbidcnt"));
                d.DanHoChk = "0";
                for (int i = 0; i < 5; i++)
                {
                    string n = (i + 1).ToString();
                    d.OfferHo[i] = Dec(r.GetFieldData("OutBlock", "offerho" + n));
                    d.BidHo[i] = Dec(r.GetFieldData("OutBlock", "bidho" + n));
                    d.OfferRem[i] = Lng(r.GetFieldData("OutBlock", "offerrem" + n));
                    d.BidRem[i] = Lng(r.GetFieldData("OutBlock", "bidrem" + n));
                    d.OfferCnt[i] = Lng(r.GetFieldData("OutBlock", "offerno" + n));
                    d.BidCnt[i] = Lng(r.GetFieldData("OutBlock", "bidno" + n));
                }
                d.ReceivedAt = recvAt;
                d.RecvTicks = recvTicks;
                var h = OnDepth; if (h != null) h(d);
            }
            catch (Exception ex) { _log("[OVH] 파싱 오류: " + ex.Message); }
        }

        /// <summary>probe용 해외선물 OVC 구독 — 심볼 유효성+시세 개통 실측 (COM 해제는 프로세스 종료에 맡김).</summary>
        public void ProbeOverseasSubscribe(string symbol)
        {
            try
            {
                var r = new XAReal();
                r.ReceiveRealData += delegate (string tr)
                {
                    if (tr != "OVC") return;
                    try
                    {
                        _log($"[OVC] {r.GetFieldData("OutBlock", "symbol")} 한국시각{r.GetFieldData("OutBlock", "kortm")} @{r.GetFieldData("OutBlock", "curpr")} 체결{r.GetFieldData("OutBlock", "trdq")} 구분{r.GetFieldData("OutBlock", "cgubun")}");
                    }
                    catch (Exception ex) { _log("[OVC] 파싱: " + ex.Message); }
                };
                r.ResFileName = ResPath.Get("OVC.res");
                r.SetFieldData("InBlock", "symbol", symbol);
                r.AdviseRealData();
                _log($"[probe] OVC 구독 시도: {symbol}");
            }
            catch (Exception ex) { _log($"[probe] OVC {symbol} 구독 실패: " + ex.Message); }
        }

        /// <summary>파생종목마스터 조회 (t8435, gubun=MF 선물/MO 옵션) — 미니200 등 t9943 미수록 종목 확보용.</summary>
        public void RequestDerivMaster(string gubun = "MF")
        {
            try
            {
                _t8435.LoadFromResFile(ResPath.Get("t8435.res"));
                _t8435.SetFieldData("t8435InBlock", "gubun", 0, gubun);
                _t8435.Request(false);
            }
            catch (Exception ex) { _log("[t8435] 조회 실패: " + ex.Message); }
        }

        private void OnRecv_t8435(string szTrCode)
        {
            if (szTrCode != "t8435") return;
            try
            {
                var items = new List<T9943Item>();
                int count = _t8435.GetBlockCount("t8435OutBlock");
                for (int i = 0; i < count; i++)
                {
                    var it = new T9943Item();
                    it.Hname = Trimmed(_t8435.GetFieldData("t8435OutBlock", "hname", i));
                    it.Shcode = Trimmed(_t8435.GetFieldData("t8435OutBlock", "shcode", i));
                    it.Expcode = Trimmed(_t8435.GetFieldData("t8435OutBlock", "expcode", i));
                    items.Add(it);
                }
                _log($"[t8435] 파생 마스터 {items.Count}건 수신");
                var h = OnDerivMaster; if (h != null) h(items);
            }
            catch (Exception ex) { _log("[t8435] 파싱 오류: " + ex.Message); }
        }

        public void RequestMaster(string gubun = "")
        {
            try
            {
                _t9943.LoadFromResFile(ResPath.Get("t9943.res"));
                _t9943.SetFieldData("t9943InBlock", "gubun", 0, gubun);
                _t9943.Request(false);
            }
            catch (Exception ex) { _log("[t9943] 조회 실패: " + ex.Message); }
        }

        /// <summary>주간(FC9/FH9)+야간(DC0/DH0) 4종 동시 구독 — 열린 세션의 TR만 데이터가 흐른다.</summary>
        public void Subscribe(string futcode)
        {
            try
            {
                _fc9.ResFileName = ResPath.Get("FC9.res");
                _fc9.SetFieldData("InBlock", "futcode", futcode);
                _fc9.AdviseRealData();
                _fh9.ResFileName = ResPath.Get("FH9.res");
                _fh9.SetFieldData("InBlock", "futcode", futcode);
                _fh9.AdviseRealData();
                _dc0.ResFileName = ResPath.Get("DC0.res");
                _dc0.SetFieldData("InBlock", "futcode", futcode);
                _dc0.AdviseRealData();
                _dh0.ResFileName = ResPath.Get("DH0.res");
                _dh0.SetFieldData("InBlock", "futcode", futcode);
                _dh0.AdviseRealData();
                _log($"[xing] 주간(FC9/FH9)+야간(DC0/DH0) 구독 시작: {futcode}");
            }
            catch (Exception ex) { _log("[xing] 구독 실패: " + ex.Message); }
        }

        public void Unsubscribe()
        {
            try { _fc9.UnadviseRealData(); } catch { }
            try { _fh9.UnadviseRealData(); } catch { }
            try { _dc0.UnadviseRealData(); } catch { }
            try { _dh0.UnadviseRealData(); } catch { }
        }

        private void OnRecv_t9943(string szTrCode)
        {
            if (szTrCode != "t9943") return;
            try
            {
                var items = new List<T9943Item>();
                int count = _t9943.GetBlockCount("t9943OutBlock");
                for (int i = 0; i < count; i++)
                {
                    var it = new T9943Item();
                    it.Hname = Trimmed(_t9943.GetFieldData("t9943OutBlock", "hname", i));
                    it.Shcode = Trimmed(_t9943.GetFieldData("t9943OutBlock", "shcode", i));
                    it.Expcode = Trimmed(_t9943.GetFieldData("t9943OutBlock", "expcode", i));
                    items.Add(it);
                }
                _log($"[t9943] 지수선물 마스터 {items.Count}건 수신");
                var h = OnMaster; if (h != null) h(items);
            }
            catch (Exception ex) { _log("[t9943] 파싱 오류: " + ex.Message); }
        }

        /// <summary>체결 파서 — FC9(주간)/DC0(야간) 필드 동일(2026-07-03 res 실측)이라 공용.</summary>
        private void ParseTick(XAReal _fc9)
        {
            long recvTicks = Stopwatch.GetTimestamp();   // 파싱 전 스탬프
            var recvAt = DateTime.Now;
            try
            {
                var d = new FC9Data();
                d.Futcode = _fc9.GetFieldData("OutBlock", "futcode");
                d.CheTime = _fc9.GetFieldData("OutBlock", "chetime");
                d.Sign = _fc9.GetFieldData("OutBlock", "sign");
                d.Change = Dec(_fc9.GetFieldData("OutBlock", "change"));
                d.Drate = Dec(_fc9.GetFieldData("OutBlock", "drate"));
                d.Price = Dec(_fc9.GetFieldData("OutBlock", "price"));
                d.Open = Dec(_fc9.GetFieldData("OutBlock", "open"));
                d.High = Dec(_fc9.GetFieldData("OutBlock", "high"));
                d.Low = Dec(_fc9.GetFieldData("OutBlock", "low"));
                d.CGubun = _fc9.GetFieldData("OutBlock", "cgubun");
                d.CVolume = Lng(_fc9.GetFieldData("OutBlock", "cvolume"));
                d.Volume = Lng(_fc9.GetFieldData("OutBlock", "volume"));
                d.Value = Lng(_fc9.GetFieldData("OutBlock", "value"));
                d.MdVolume = Lng(_fc9.GetFieldData("OutBlock", "mdvolume"));
                d.MdCheCnt = Lng(_fc9.GetFieldData("OutBlock", "mdchecnt"));
                d.MsVolume = Lng(_fc9.GetFieldData("OutBlock", "msvolume"));
                d.MsCheCnt = Lng(_fc9.GetFieldData("OutBlock", "mschecnt"));
                d.CPower = Dec(_fc9.GetFieldData("OutBlock", "cpower"));
                d.OfferHo1 = Dec(_fc9.GetFieldData("OutBlock", "offerho1"));
                d.BidHo1 = Dec(_fc9.GetFieldData("OutBlock", "bidho1"));
                d.OpenYak = Lng(_fc9.GetFieldData("OutBlock", "openyak"));
                d.K200Jisu = Dec(_fc9.GetFieldData("OutBlock", "k200jisu"));
                d.TheoryPrice = Dec(_fc9.GetFieldData("OutBlock", "theoryprice"));
                d.Kasis = Dec(_fc9.GetFieldData("OutBlock", "kasis"));
                d.SBasis = Dec(_fc9.GetFieldData("OutBlock", "sbasis"));
                d.IBasis = Dec(_fc9.GetFieldData("OutBlock", "ibasis"));
                d.OpenYakCha = Lng(_fc9.GetFieldData("OutBlock", "openyakcha"));
                d.JGubun = _fc9.GetFieldData("OutBlock", "jgubun");
                d.JnilVolume = Lng(_fc9.GetFieldData("OutBlock", "jnilvolume"));
                d.ReceivedAt = recvAt;
                d.RecvTicks = recvTicks;
                var h = OnTick; if (h != null) h(d);
            }
            catch (Exception ex) { _log("[FC9] 파싱 오류: " + ex.Message); }
        }

        /// <summary>호가 파서 — FH9(주간)/DH0(야간) 필드 동일이라 공용.</summary>
        private void ParseDepth(XAReal _fh9)
        {
            long recvTicks = Stopwatch.GetTimestamp();
            var recvAt = DateTime.Now;
            try
            {
                var d = new FH9Data();
                d.Futcode = _fh9.GetFieldData("OutBlock", "futcode");
                d.HoTime = _fh9.GetFieldData("OutBlock", "hotime");
                d.TotOfferRem = Lng(_fh9.GetFieldData("OutBlock", "totofferrem"));
                d.TotBidRem = Lng(_fh9.GetFieldData("OutBlock", "totbidrem"));
                d.TotOfferCnt = Lng(_fh9.GetFieldData("OutBlock", "totoffercnt"));
                d.TotBidCnt = Lng(_fh9.GetFieldData("OutBlock", "totbidcnt"));
                d.DanHoChk = _fh9.GetFieldData("OutBlock", "danhochk");
                for (int i = 0; i < 5; i++)
                {
                    string n = (i + 1).ToString();
                    d.OfferHo[i] = Dec(_fh9.GetFieldData("OutBlock", "offerho" + n));
                    d.BidHo[i] = Dec(_fh9.GetFieldData("OutBlock", "bidho" + n));
                    d.OfferRem[i] = Lng(_fh9.GetFieldData("OutBlock", "offerrem" + n));
                    d.BidRem[i] = Lng(_fh9.GetFieldData("OutBlock", "bidrem" + n));
                    d.OfferCnt[i] = Lng(_fh9.GetFieldData("OutBlock", "offercnt" + n));
                    d.BidCnt[i] = Lng(_fh9.GetFieldData("OutBlock", "bidcnt" + n));
                }
                d.ReceivedAt = recvAt;
                d.RecvTicks = recvTicks;
                var h = OnDepth; if (h != null) h(d);
            }
            catch (Exception ex) { _log("[FH9] 파싱 오류: " + ex.Message); }
        }

        private static string Trimmed(string s) { return s == null ? "" : s.Trim(); }
        private static decimal Dec(string s) { decimal v; return decimal.TryParse(s == null ? null : s.Trim(), out v) ? v : 0m; }
        private static long Lng(string s) { long v; return long.TryParse(s == null ? null : s.Trim(), out v) ? v : 0L; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Unsubscribe();
            foreach (var r in _ovsReals) { try { r.UnadviseRealData(); Marshal.ReleaseComObject(r); } catch { } }
            foreach (var o in new object[] { _fc9, _fh9, _dc0, _dh0, _t9943, _t8435 })
            {
                if (o != null) { try { Marshal.ReleaseComObject(o); } catch { } }
            }
        }
    }
}
