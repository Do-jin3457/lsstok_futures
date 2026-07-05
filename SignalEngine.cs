using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LS.Futures
{
    /// <summary>가상 체결 1건 (shadow — 실주문 아님).</summary>
    public sealed class PaperFill
    {
        public string Code;      // 종목코드 (다중종목 v5)
        public string Time;      // HH:mm:ss.fff
        public string Mode;      // "V1"
        public string Side;      // LONG / SHORT
        public string Kind;      // ENTRY / TIME / DISASTER / EOD
        public double Price;
        public double PnlTicks;  // 청산 시 확정 (진입=0)
        public double Score;
        public string Reason;
    }

    /// <summary>가상 포지션.</summary>
    public sealed class PaperPosition
    {
        public string Side;
        public double Entry;
        public double EntrySpread;   // 진입 시점 스프레드(재해 SL 기준)
        public long OpenedMs;
        public double Score;
        public double UnrealizedTicks;
    }

    /// <summary>
    /// 신호 엔진 v1 (2026-07-03 확정 — 7/3 풀데이 counterfactual 근거, memory 참조).
    ///   진입: |score|≥0.6 (score = OFI z + 5단 QI + micro + 괴리율[약세 차단, 룰8])
    ///   청산: ①5분 고정 보유(TIME) ②재해 SL −3×진입스프레드(DISASTER) ③세션 마감 5분 전 강제청산(EOD)
    ///   가드: 단일가 금지 / 마감 10분 전 신규 금지 / 일손실 상한 도달 시 당일 중지 / 쿨다운 10초
    ///   v0의 TP3/SL2 배리어는 폐지 — 광폭 스프레드 환경에서 수학적 사망 실증(-1,716틱 리플레이).
    ///   세션: 주간 08:45~15:45 + KRX 야간 18:00~익일 06:00. 전부 shadow(가상 1계약).
    ///   ⚠️ 파라미터는 단일일(+6.3% 강세일) 백테스트 근거 — 약세·횡보 미검증(룰 8), 실주문 금지.
    /// </summary>
    public sealed class SignalEngine
    {
        // ── v1 파라미터 ──
        public const double WOfi = 0.35, WQi = 0.25, WMicro = 0.20, WBasis = 0.20;
        public const double EnterTh = 0.60;          // 진입 |score|
        public const int HoldMs = 300000;            // 5분 고정 보유
        public const double DisasterSpreadMult = 3;  // 재해 SL = −3×진입 스프레드
        public const int CooldownMs = 10000;
        public const int NoEntryBeforeCloseMin = 10; // 마감 N분 전 신규 금지
        public const int ForceCloseBeforeCloseMin = 5;
        public const double DailyLossCapTicks = -100; // 일손실 상한(도달 시 당일 신규 중지)
        // 종목별 스펙(다중종목 v5): 틱단위/틱가치/수수료/세션 규칙은 InstrumentConfig로 주입.
        private readonly double _tick;
        private readonly double _tickKrw;
        private readonly double _commissionRt;    // 왕복 정액(해외)
        private readonly double _commPct;         // 편도 정률(미니 0.003%)
        private readonly double _mult;            // 약정금액 승수 = TickValueKrw/TickSize (미니 50,000원/pt)
        private double _dayCommission;            // 당일 실수수료 누적(정률=거래별 약정금액 반영)
        private readonly bool _overseas;
        private readonly string _instName;
        private readonly string _instCode;
        private readonly double _staleThreshSec;   // 체결 무수신 세션종료 판정(해외 90 / 국내 300 — 얇은 야간 오탐 방지)
        private bool _stale;                        // 무수신 상태(신규진입 금지 플래그)

        private readonly Action<string> _log;
        private PaperPosition _pos;
        private long _cooldownUntil;
        private double _dayPnlTicks;
        private string _dayKey = "";
        private int _trades, _wins;
        private bool _dayCapHit;
        private readonly List<PaperFill> _fills = new List<PaperFill>();
        private const int FillsMax = 200;
        public event Action<PaperFill> OnFill;

        public double LastScore;
        public string LastScoreBreakdown = "";
        public string SessionState = "—";

        public SignalEngine(Action<string> log, InstrumentConfig cfg)
        {
            _log = log ?? (_ => { });
            _tick = cfg.TickSize;
            _tickKrw = cfg.TickValueKrw;
            _commissionRt = cfg.CommissionRt;
            _commPct = cfg.CommissionPct;
            _mult = cfg.TickSize > 0 ? cfg.TickValueKrw / cfg.TickSize : 0;
            _overseas = cfg.Overseas;
            _instName = cfg.Name ?? cfg.Code;
            _instCode = cfg.Code;
            _staleThreshSec = cfg.Overseas ? 90 : 300;
            _trThrFrac = cfg.TrThrFrac; _trHoldMin = cfg.TrHoldMin; _trErThr = cfg.TrErThr; _trKAtr = cfg.TrKAtr;
        }

        /// <summary>재시작 시 당일 확정 성적 복원(v5.1 — 재시작 부정합 fix, 회원 지적 7/4).</summary>
        public void RestoreDay(double pnlTicks, int trades, int wins)
        {
            _dayKey = FuturesRecorder.SessionTradeDate(DateTime.Now);   // v5.3: 거래일 정의 통일(자정 경계)
            _dayPnlTicks = pnlTicks;
            _trades = trades;
            _wins = wins;
            if (_dayPnlTicks <= DailyLossCapTicks) _dayCapHit = true;
            _log($"[signal] {_instName} 당일 성적 복원: {pnlTicks:+0.0;-0.0}틱 {trades}건");
        }

        /// <summary>재시작 시 대시보드 체결내역 복원(v5.2 — 재시작 후 체결창 빈 화면 fix, 회원 지적 7/4).
        /// _fills는 in-memory ring이라 프로세스 재기동 시 증발 → DB의 당일 최근 fills를 시간순으로 재적재.</summary>
        public void RestoreFills(IEnumerable<PaperFill> fills)
        {
            if (fills == null) return;
            int n = 0;
            foreach (var f in fills)
            {
                _fills.Add(f);
                if (_fills.Count > FillsMax) _fills.RemoveAt(0);
                n++;
            }
            if (n > 0) _log($"[signal] {_instName} 체결내역 복원: {n}건");
        }

        private static long NowMs() { return System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000); }
        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

        /// <summary>
        /// 세션 판정. KRX: 주간 08:45~15:45 / 야간 18:00~익일 06:00. CME(해외): 07:00~익일 06:00 연속(주말은 데이터 부재로 자연 휴면).
        /// 반환=세션 내 여부, minToClose=마감까지 분.
        /// </summary>
        private bool InSession(DateTime now, out double minToClose)
        {
            double m = now.Hour * 60 + now.Minute + now.Second / 60.0;
            if (_overseas)
            {
                // CME: 한국시간 07:00(420) 개장 ~ 익일 06:00(360) 마감 (06:00~07:00 정비 브레이크)
                if (m >= 420) { minToClose = (1440 - m) + 360; return true; }
                if (m < 360) { minToClose = 360 - m; return true; }
                minToClose = 0; return false;
            }
            // 주간: 08:45(525) ~ 15:45(945)
            if (m >= 525 && m < 945) { minToClose = 945 - m; return true; }
            // 야간: 18:00(1080) ~ 24:00 → 마감은 익일 06:00(360)
            if (m >= 1080) { minToClose = (1440 - m) + 360; return true; }
            // 새벽: 00:00 ~ 06:00(360) = 전일 야간 세션의 연속
            if (m < 360) { minToClose = 360 - m; return true; }
            minToClose = 0; return false;
        }

        public void Step(FeatureSnapshot s)
        {
            if (s == null || s.BestBid <= 0 || s.BestAsk <= 0) return;

            // score (v0와 동일 합성 — 7/3 검증에서 신호 자체는 유효 판정)
            double cOfi = WOfi * Math.Tanh(s.Ofi5sZ / 2.0);
            double cQi = WQi * s.QiW5;
            double cMicro = WMicro * Clamp(s.MicroDevTicks, -1, 1);
            double cBasis;
            if (_overseas)
            {
                // 해외(OVC): kasis/k200jisu 미제공 → basis 성분 구조적 부재. 나머지 3성분을 합1로 재정규화.
                // (안 하면 score 상한이 0.80으로 눌려 |s|≥0.6 도달이 구조적 희박 — 7/4 해외 거래 0건 원인.)
                double scale = 1.0 / (WOfi + WQi + WMicro);
                cOfi *= scale; cQi *= scale; cMicro *= scale;
                cBasis = 0;
            }
            else
            {
                cBasis = s.Bear ? 0 : WBasis * Clamp(-s.BasisZ / 2.0, -1, 1);   // 약세는 basis 억제 유지(룰 8) — 재정규화 안 함(의도적 진입 보수화)
            }
            double score = cOfi + cQi + cMicro + cBasis;
            LastScore = score;
            LastScoreBreakdown = $"ofi={cOfi:F2} qi={cQi:F2} micro={cMicro:F2} basis={cBasis:F2}"
                + (_overseas ? " [해외:basis부재·재정규화]" : (s.Bear ? " [BEAR:basis차단]" : ""));

            var wall = DateTime.Now;
            double minToClose;
            bool inSess = InSession(wall, out minToClose);
            SessionState = !inSess ? "휴장" : (minToClose <= ForceCloseBeforeCloseMin ? "마감청산구간" : (minToClose <= NoEntryBeforeCloseMin ? "신규금지구간" : "거래중"));

            // 거래일 전환 시 일손익 리셋 — 세션 거래일(06시 귀속) 기준. 06:00 넘겨 새 거래일 진입 시 1회 리셋(v5.3).
            string dk = FuturesRecorder.SessionTradeDate(wall);
            if (dk != _dayKey) { _dayKey = dk; _dayPnlTicks = 0; _dayCapHit = false; }

            // 추세추종 v1/v2 실시간 shadow (기존 V1 호가모드와 독립 병행 — 대시보드 관측용)
            TrendStep(s, inSess, minToClose, dk);

            long now = NowMs();

            if (_pos != null)
            {
                double exitPx = _pos.Side == "LONG" ? s.BestBid : s.BestAsk;   // 상대호가 청산(보수)
                double pnl = (_pos.Side == "LONG" ? exitPx - _pos.Entry : _pos.Entry - exitPx) / _tick;
                _pos.UnrealizedTicks = pnl;

                string kind = null;
                double disasterTicks = -DisasterSpreadMult * (_pos.EntrySpread / _tick);
                if (pnl <= disasterTicks) kind = "DISASTER";
                else if (now - _pos.OpenedMs >= HoldMs) kind = "TIME";
                else if (!inSess || minToClose <= ForceCloseBeforeCloseMin) kind = "EOD";
                if (kind == null) return;

                CloseAt(exitPx, pnl, kind, score, s.Stamp);
                return;
            }

            // ── 진입 가드 ──
            if (!inSess || minToClose <= NoEntryBeforeCloseMin) return;   // 휴장/마감 임박
            if (_stale) return;                                          // 체결 무수신(조기폐장/거래중단) — 신규 진입 금지
            if (s.DanHo) return;                                          // 단일가
            if (_dayCapHit) return;                                       // 일손실 상한
            if (now < _cooldownUntil) return;
            if (Math.Abs(score) < EnterTh) return;

            string side = score > 0 ? "LONG" : "SHORT";
            double entryPx = side == "LONG" ? s.BestAsk : s.BestBid;      // taker 진입(7/3 검증: 5분 보유가 비용 흡수)
            _pos = new PaperPosition { Side = side, Entry = entryPx, EntrySpread = s.Spread, OpenedMs = now, Score = score };
            Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = "V1", Side = side, Kind = "ENTRY", Price = entryPx, PnlTicks = 0, Score = score, Reason = LastScoreBreakdown });
            _log($"[signal] V1 {side} 진입 @{entryPx:F2} score={score:F2} spread={s.Spread:F2}");
        }

        /// <summary>포지션 청산 확정 — Step(정상 청산)과 OnHeartbeat(무수신 청산)가 공용. 성적/일손실cap/기록/쿨다운 일괄.</summary>
        private void CloseAt(double exitPx, double pnl, string kind, double score, string stamp)
        {
            _dayPnlTicks += pnl;
            _trades++;
            if (pnl > 0) _wins++;
            // 왕복 수수료: 정률(미니)=진입·청산 각 약정금액×pct / 정액(해외)=CommissionRt
            _dayCommission += _commPct > 0 ? (_pos.Entry + exitPx) * _mult * _commPct : _commissionRt;
            if (_dayPnlTicks <= DailyLossCapTicks && !_dayCapHit)
            {
                _dayCapHit = true;
                _log($"[signal] 🔴 일손실 상한 도달({_dayPnlTicks:F0}틱) — 당일 신규 진입 중지");
            }
            Record(new PaperFill { Code = _instCode, Time = stamp, Mode = "V1", Side = _pos.Side, Kind = kind, Price = exitPx, PnlTicks = pnl, Score = score, Reason = LastScoreBreakdown });
            _log($"[signal] V1 {_pos.Side} {kind} @{exitPx:F2} pnl={pnl:+0.0;-0.0}틱 (일누적 {_dayPnlTicks:+0.0;-0.0}틱)");
            _pos = null;
            _cooldownUntil = NowMs() + CooldownMs;
        }

        /// <summary>매초 하트비트(체결 무수신 감지) — 조기폐장/거래중단을 시각 캘린더 없이 포착(회원 결정 7/4).
        /// depth도 멈춰 Step이 안 도는 상황 대비: 여기서 직접 청산 + 신규진입 금지(_stale). InstrumentPipeline.TickRates가 호출.</summary>
        public void OnHeartbeat(double sinceTickSec, FeatureSnapshot s)
        {
            bool wasStale = _stale;
            _stale = sinceTickSec >= _staleThreshSec;
            if (_stale && _pos != null && s != null && s.BestBid > 0 && s.BestAsk > 0)
            {
                double exitPx = _pos.Side == "LONG" ? s.BestBid : s.BestAsk;
                double pnl = (_pos.Side == "LONG" ? exitPx - _pos.Entry : _pos.Entry - exitPx) / _tick;
                _log($"[signal] {_instName} 체결 무수신 {sinceTickSec:F0}s → 세션종료 간주");
                CloseAt(exitPx, pnl, "STALE", _pos.Score, s.Stamp);
            }
            if (_stale && !wasStale) SessionState = "무수신(세션종료?)";
        }

        private void Record(PaperFill f)
        {
            _fills.Add(f);
            if (_fills.Count > FillsMax) _fills.RemoveAt(0);
            var h = OnFill; if (h != null) h(f);
        }

        // ═══════════════════════════════════════════════════════════════
        //  추세추종 v1/v2 실시간 shadow 모드 (2026-07-04, 회원 지시 — 대시보드 실시간 관측용)
        //  v1 = 10분 모멘텀 추세추종(가격) / v2 = v1 + 호가필터(QI·micro·OFI 동일방향)
        //  가격버퍼(1분 샘플)→모멘텀/ER/ATR. 별도 paper 포지션·성적. 실주문 아님.
        //  재시작 시 리셋(shadow 관측 v1 — restore는 추후). REF 1300=미니 기준 THR 스케일.
        // ═══════════════════════════════════════════════════════════════
        private const int TrLookbackMin = 10, TrErWin = 30, TrAtrWin = 14;
        private readonly int _trHoldMin;                       // 종목별 config (Part A)
        private readonly double _trThrFrac, _trErThr, _trKAtr; // 종목별 config (Part A)
        private readonly List<double[]> _mids = new List<double[]>();  // [ms, mid] 1분 샘플

        // ── 첫30분 레인지 게이트(관측전용, 2026-07-05) ──
        // 지수(IDX001) 84일 워크포워드 검증에선 유의(스킵일 p=0.0028)했으나 실제 미니선물 9일 실데이터로
        // 대조 시 재현 안 됨(표본 9일이 너무 작아 판정 불가 상태) — 그래서 실거래엔 미반영, 매일 판단만
        // 로그에 쌓아서 미니선물 실데이터가 30~40일 이상 모이면 같은 워크포워드 방식으로 재검증할 것.
        private string _gateDayKey = "";
        private double _gateOpenPx, _gateHigh30, _gateLow30;
        private long _gateDayStartMs;
        private bool _gateLocked;
        private double _gateE30 = -1;
        private bool? _gateDecision;
        private string GateLogPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "gate_shadow_log.csv");

        private void ShadowGateStep(double mid, long now, string dk)
        {
            if (dk != _gateDayKey)
            {
                if (_gateDayKey != "" && _gateLocked)
                    AppendGateLog(_gateDayKey, _gateE30, _gateDecision == true, _trV1.DayPnlTicks, _trV2.DayPnlTicks);
                _gateDayKey = dk; _gateOpenPx = mid; _gateHigh30 = mid; _gateLow30 = mid;
                _gateDayStartMs = now; _gateLocked = false; _gateE30 = -1; _gateDecision = null;
            }
            if (_gateLocked || mid <= 0) return;
            if (mid > _gateHigh30) _gateHigh30 = mid;
            if (mid < _gateLow30) _gateLow30 = mid;
            if (now - _gateDayStartMs < 30L * 60000) return;
            _gateE30 = _gateOpenPx > 0 ? (_gateHigh30 - _gateLow30) / _gateOpenPx : 0;
            double median = LoadHistMedianE30();
            _gateDecision = median <= 0 || _gateE30 >= median;
            _gateLocked = true;
            string verdict = _gateDecision == true ? "통과권고" : "스킵권고";
            _log($"[gate] {dk} 첫30분레인지={_gateE30 * 100:F2}% 기준중앙값={median * 100:F2}% -> {verdict} (관측전용, 실거래 미영향)");
        }

        private double LoadHistMedianE30()
        {
            try
            {
                if (!File.Exists(GateLogPath)) return -1;
                var vals = new List<double>();
                foreach (var line in File.ReadAllLines(GateLogPath))
                {
                    var f = line.Split(',');
                    if (f.Length >= 2 && double.TryParse(f[1], out double e30)) vals.Add(e30);
                }
                if (vals.Count < 5) return -1;
                vals.Sort();
                int mid2 = vals.Count / 2;
                return vals.Count % 2 == 0 ? (vals[mid2 - 1] + vals[mid2]) / 2.0 : vals[mid2];
            }
            catch (Exception ex) { _log("[gate] 히스토리 로드 실패: " + ex.Message); return -1; }
        }

        private void AppendGateLog(string date, double e30, bool gatePass, double v1PnlTicks, double v2PnlTicks)
        {
            try
            {
                var dir = Path.GetDirectoryName(GateLogPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(GateLogPath, $"{date},{e30:F6},{gatePass},{v1PnlTicks:F2},{v2PnlTicks:F2}\n");
            }
            catch (Exception ex) { _log("[gate] 로그 기록 실패: " + ex.Message); }
        }
        private long _lastMidMs;

        private sealed class TrendMode
        {
            public readonly string Name, BaseLabel;
            public readonly bool UseFilter;
            public PaperPosition Pos;
            public double DayPnlTicks, DayCommission;
            public int Trades, Wins;
            public string DayKey = "";
            public TrendMode(string name, string label, bool filter) { Name = name; BaseLabel = label; UseFilter = filter; }
        }
        private readonly TrendMode _trV1 = new TrendMode("TREND", "추세추종 v1(10분모멘텀)", false);
        private readonly TrendMode _trV2 = new TrendMode("TREND+OB", "추세+호가필터 v2", true);

        private void TrendStep(FeatureSnapshot s, bool inSess, double minToClose, string dk)
        {
            double mid = (s.BestBid + s.BestAsk) / 2.0;
            long now = NowMs();
            if (now - _lastMidMs >= 60000 || _mids.Count == 0)   // 1분 샘플 + 36분 프루닝
            {
                _mids.Add(new double[] { now, mid });
                _lastMidMs = now;
                long cut = now - 36L * 60000;
                while (_mids.Count > 0 && _mids[0][0] < cut) _mids.RemoveAt(0);
            }
            ShadowGateStep(mid, now, dk);   // 관측전용 — HandleTrend가 dk 전환시 DayPnlTicks 리셋하기 전에 먼저 캡처
            double mom = Momentum(now, mid, TrLookbackMin);
            double thrPt = mid * _trThrFrac;
            double er = EffRatio(TrErWin);
            double atrPt = AtrPt(TrAtrWin);
            HandleTrend(_trV1, s, mom, thrPt, er, atrPt, inSess, minToClose, dk);
            HandleTrend(_trV2, s, mom, thrPt, er, atrPt, inSess, minToClose, dk);
        }

        private double Momentum(long now, double mid, int lookbackMin)
        {
            if (_mids.Count == 0) return 0;
            long target = now - (long)lookbackMin * 60000;
            bool found = false; double past = 0;
            for (int i = _mids.Count - 1; i >= 0; i--) { if (_mids[i][0] <= target) { past = _mids[i][1]; found = true; break; } }
            return found ? mid - past : 0;   // 10분치 없으면 0(진입 안 함)
        }
        private const int ErSmooth = 3;   // 끝점 평활화 봉수 — 2026-07-05: 끝점 단일값은 마지막 1봉 반등에 취약(실측: 29분 진짜 추세를 1봉 반등이 "횡보"로 오판시킴)
        private double EffRatio(int win)
        {
            int n = _mids.Count; if (n < win + ErSmooth) return -1;   // 데이터부족(워밍업~33분) → 미달값(< _trErThr 자연히 걸려 진입스킵). 이전엔 1.0(="확실 추세")이라 워밍업마다 필터 무력화되던 버그(2026-07-05 실측: 재백테스트서 워밍업 31건이 필터없이 진입해 net 과대집계).
            double endAvg = 0; for (int i = n - ErSmooth; i < n; i++) endAvg += _mids[i][1]; endAvg /= ErSmooth;
            double startAvg = 0; for (int i = n - win - ErSmooth; i < n - win; i++) startAvg += _mids[i][1]; startAvg /= ErSmooth;
            double net = Math.Abs(endAvg - startAvg);
            double tot = 0; for (int i = n - win; i < n; i++) tot += Math.Abs(_mids[i][1] - _mids[i - 1][1]);
            return tot > 0 ? net / tot : 0;
        }
        private double AtrPt(int win)
        {
            int n = _mids.Count; if (n < 2) return 0;
            int lo = Math.Max(1, n - win); double sum = 0; int c = 0;
            for (int i = lo; i < n; i++) { sum += Math.Abs(_mids[i][1] - _mids[i - 1][1]); c++; }
            return c > 0 ? sum / c : 0;
        }

        private void HandleTrend(TrendMode m, FeatureSnapshot s, double mom, double thrPt, double er, double atrPt, bool inSess, double minToClose, string dk)
        {
            if (m.DayKey != dk) { m.DayKey = dk; m.DayPnlTicks = 0; m.DayCommission = 0; }
            long now = NowMs();
            if (m.Pos != null)
            {
                double exitPx = m.Pos.Side == "LONG" ? s.BestBid : s.BestAsk;
                double pnlPt = m.Pos.Side == "LONG" ? exitPx - m.Pos.Entry : m.Pos.Entry - exitPx;
                m.Pos.UnrealizedTicks = pnlPt / _tick;
                double stopPt = _trKAtr * atrPt;
                string kind = null;
                if (stopPt > 0 && pnlPt <= -stopPt) kind = "STOP";
                else if (now - m.Pos.OpenedMs >= (long)_trHoldMin * 60000) kind = "TIME";
                else if (!inSess || minToClose <= ForceCloseBeforeCloseMin) kind = "EOD";
                if (kind == null) return;
                TrendClose(m, exitPx, pnlPt / _tick, kind, s.Stamp);
                return;
            }
            if (!inSess || minToClose <= NoEntryBeforeCloseMin) return;
            if (_stale || s.DanHo) return;
            if (Math.Abs(mom) < thrPt) return;        // 추세 약함
            if (er < _trErThr) return;                // 횡보
            int side = mom > 0 ? 1 : -1;
            if (m.UseFilter && !ObAgree(s, side)) return;   // v2 호가필터
            string sideStr = side > 0 ? "LONG" : "SHORT";
            double entryPx = side > 0 ? s.BestAsk : s.BestBid;
            m.Pos = new PaperPosition { Side = sideStr, Entry = entryPx, EntrySpread = s.Spread, OpenedMs = now, Score = mom };
            Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = sideStr, Kind = "ENTRY", Price = entryPx, PnlTicks = 0, Score = mom, Reason = $"mom={mom:F2} er={er:F2}" });
        }

        private static bool ObAgree(FeatureSnapshot s, int side)
        {
            bool up = side > 0;
            return (s.QiW5 > 0) == up && (s.MicroDevTicks > 0) == up && (s.Ofi5sZ > 0) == up;
        }

        private void TrendClose(TrendMode m, double exitPx, double pnlTicks, string kind, string stamp)
        {
            m.DayPnlTicks += pnlTicks; m.Trades++; if (pnlTicks > 0) m.Wins++;
            m.DayCommission += _commPct > 0 ? (m.Pos.Entry + exitPx) * _mult * _commPct : _commissionRt;
            Record(new PaperFill { Code = _instCode, Time = stamp, Mode = m.Name, Side = m.Pos.Side, Kind = kind, Price = exitPx, PnlTicks = pnlTicks, Score = m.Pos.Score, Reason = "" });
            m.Pos = null;
        }

        private object TrendModeState(TrendMode m)
        {
            var p = m.Pos;
            return new
            {
                mode = m.Name,
                label = $"{_instName} {m.BaseLabel} [{SessionState}]",
                position = p == null ? null : (object)new { side = p.Side, entry = p.Entry, unrealizedTicks = p.UnrealizedTicks, score = p.Score },
                pnlTicks = m.DayPnlTicks,
                pnlKrw = m.DayPnlTicks * _tickKrw - m.DayCommission,
                trades = m.Trades,
                winRate = m.Trades > 0 ? (double)m.Wins / m.Trades * 100 : 0
            };
        }

        /// <summary>API 상태 — camelCase(직렬화 경계 규율). 대시보드 modes 배열 계약 유지(V1 + 추세 v1/v2).</summary>
        public object GetState()
        {
            var p = _pos;
            var modes = new List<object>
            {
                new
                {
                    mode = "V1",
                    label = $"{_instName} 5분보유·EOD청산 [{SessionState}]",
                    position = p == null ? null : new { side = p.Side, entry = p.Entry, unrealizedTicks = p.UnrealizedTicks, score = p.Score },
                    pnlTicks = _dayPnlTicks,
                    pnlKrw = _dayPnlTicks * _tickKrw - _dayCommission,
                    trades = _trades,
                    winRate = _trades > 0 ? (double)_wins / _trades * 100 : 0
                }
            };
            modes.Add(TrendModeState(_trV1));   // 추세추종 v1 (실시간 shadow)
            modes.Add(TrendModeState(_trV2));   // 추세+호가필터 v2
            var recent = new List<PaperFill>();
            int start = Math.Max(0, _fills.Count - 30);
            for (int i = _fills.Count - 1; i >= start; i--) recent.Add(_fills[i]);
            return new
            {
                score = LastScore,
                breakdown = LastScoreBreakdown,
                paramsLabel = $"⚠️ v1({_instName}, shadow): |s|≥{EnterTh} 진입 → 5분 보유 / 재해SL −{DisasterSpreadMult}×스프레드 / 마감{ForceCloseBeforeCloseMin}분전 청산 / 일손실 {DailyLossCapTicks}틱 중지 — 단일일 검증(룰8)",
                modes,
                fills = recent.ConvertAll(f => (object)new
                {
                    time = f.Time, mode = f.Mode, side = f.Side, kind = f.Kind,
                    price = f.Price, pnlTicks = f.PnlTicks, score = f.Score
                }),
                gateShadow = new
                {
                    note = "관측전용 — 실거래 미영향. 지수(84일) 워크포워드 검증 유의, 미니선물 9일 실데이터로 미확인.",
                    locked = _gateLocked,
                    e30Pct = _gateLocked ? _gateE30 * 100 : (double?)null,
                    decision = _gateDecision
                }
            };
        }
    }
}
