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
        // 진입 스프레드 상한(2026-07-09, 회원 승인): 스프레드>20틱 taker 진입은 왕복비용을 구조적으로 못 이김.
        // 실측(7/8+7/9 완결본, 미니): ≤20틱 127건 +2,518틱(승50%) vs >20틱 269건 −15,552틱(승35%) — 양 국면(chop/추세)·전모드 공통 독성.
        // 전 종목 공통 적용(해외는 스프레드 1~3틱이라 실질 미니 전용). 스킵=카운터+60초 스로틀 로그(DB 미기록, 사후검증은 tick/depth로).
        public const double MaxEntrySpreadTicks = 20;
        public const int NoEntryBeforeCloseMin = 10; // 마감 N분 전 신규 금지
        public const int ForceCloseBeforeCloseMin = 5;
        public const double DailyLossCapTicks = -100; // 일손실 상한(도달 시 당일 신규 중지) — 실거래 자본보호용
        // v5.26: shadow(실주문0) 단계에선 상한을 "관측만"(도달시각은 계속 기록하되 진입은 안 막음) — 하루종일 돌려 표본 누적이 목적이므로.
        //   회원 지적 7/6("계속 돌려서 누적해서 봐야하는데 왜 잠그냐"). 실거래 전환 시 true로 바꿔 자본보호 서킷브레이커 복원.
        public const bool EnforceDayCap = false;
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
        private long _sprSkips;            // 스프레드 상한 진입스킵 누적(전모드 합, GetState 노출)
        private long _lastSprSkipLogMs;    // 스킵 로그 60초 스로틀
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
        public void RestoreDay(double pnlTicks, int trades, int wins, double sumExitPx = 0)
        {
            _dayKey = FuturesRecorder.SessionTradeDate(DateTime.Now);   // v5.3: 거래일 정의 통일(자정 경계)
            _dayPnlTicks = pnlTicks;
            _trades = trades;
            _wins = wins;
            _dayCommission = RestoredCommission(trades, sumExitPx);   // v5.32: 수수료도 복원(재기동 후 원화 손익 과대표시 fix)
            if (_dayPnlTicks <= DailyLossCapTicks) _dayCapHit = true;
            _log($"[signal] {_instName} 당일 성적 복원: {pnlTicks:+0.0;-0.0}틱 {trades}건 수수료 {_dayCommission:F0}원");
        }

        /// <summary>재기동 수수료 재계산 — 정률(미니)=(entry+exit)≈2×exit 근사(오차 건당 1~2원), 정액(해외)=건수×왕복정액.</summary>
        private double RestoredCommission(int trades, double sumExitPx)
        {
            return _commPct > 0 ? 2 * sumExitPx * _mult * _commPct : trades * _commissionRt;
        }

        /// <summary>재시작 시 추세추종(TREND/TREND+OB) 당일 확정 성적 복원(v5.24 — V1만 복원되고 TREND류는
        /// 빠져있던 결함 fix, 재시작마다 대시보드 카드가 0으로 보이던 원인. 회원 지적 7/6).</summary>
        public void RestoreTrendDay(string mode, double pnlTicks, int trades, int wins, double sumExitPx = 0)
        {
            TrendMode m = mode == _trV1.Name ? _trV1 : (mode == _trV2.Name ? _trV2 : (mode == _v1Inv.Name ? _v1Inv : (mode == _v11.Name ? _v11 : (mode == _v12.Name ? _v12 : (mode == _best.Name ? _best : (mode == _bestV1.Name ? _bestV1 : (mode == _gap.Name ? _gap : (mode == _dfade.Name ? _dfade : (mode == _dfx.Name ? _dfx : (mode == _m30.Name ? _m30 : (mode == _v11c.Name ? _v11c : (mode == _bbz.Name ? _bbz : null))))))))))));
            if (m == null) return;
            m.DayKey = FuturesRecorder.SessionTradeDate(DateTime.Now);
            m.DayPnlTicks = pnlTicks;
            m.Trades = trades;
            m.Wins = wins;
            m.DayCommission = RestoredCommission(trades, sumExitPx);   // v5.32: 수수료 복원
            _log($"[signal] {_instName} {mode} 당일 성적 복원: {pnlTicks:+0.0;-0.0}틱 {trades}건 수수료 {m.DayCommission:F0}원");
        }

        /// <summary>재시작 시 가격버퍼(_mids) 즉시 복원(v5.25) — 안 하면 ER(횡보판별)이 재시작마다 33분 워밍업을
        /// 새로 거쳐야 해서, 재배포가 잦으면(오늘밤처럼) TREND/TREND+OB가 계속 리셋되며 오래 멈춤. DB의 최근 체결가를
        /// 1분 샘플로 넣어 워밍업을 즉시 완료 상태로 만든다.</summary>
        public void SeedMids(List<FuturesRecorder.MidSeed> seeds)
        {
            if (seeds == null || seeds.Count == 0) return;
            long now = NowMs();
            var nowWall = DateTime.Now;
            foreach (var sd in seeds)
            {
                double deltaSec = (nowWall - sd.At).TotalSeconds;
                if (deltaSec < 0) continue;   // 자정 경계 등 이상치 방어
                long ms = now - (long)(deltaSec * 1000);
                _mids.Add(new double[] { ms, sd.Price });
            }
            long cut = now - 36L * 60000;
            while (_mids.Count > 0 && _mids[0][0] < cut) _mids.RemoveAt(0);
            if (_mids.Count > 0) _lastMidMs = (long)_mids[_mids.Count - 1][0];
            _log($"[signal] {_instName} 가격버퍼 시드 복원: {_mids.Count}개 샘플");
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

            // V1-INV shadow — V1 아래 블록의 return들과 무관하게 매 Step 처리돼야 하므로 여기서 호출
            V1InvStep(s, score, inSess, minToClose, dk);
            V1VarStep(_v11, s, score, inSess, minToClose, dk);   // V1.1 (MNQ 전용, 내부 가드)
            V1VarStep(_v12, s, score, inSess, minToClose, dk);   // V1.2
            BestV1Step(s, score, inSess, minToClose, dk);        // BEST-V1 (MNQ 전용, 변동성밴드+독성시간)
            GapStep(s, inSess, minToClose, dk);                  // GAP (미니 전용, 오버나이트 갭 지속)
            DfStep(s, inSess, minToClose, dk);                   // DFADE (MNQ 전용, US 수급소진 페이드 — 7/15)
            DfxStep(s, inSess, minToClose, dk);                  // DFADE-X (MNQ 전용, 수급×호가저항 — 7/17)
            M30Step(s, inSess, minToClose, dk);                  // M30F (MNQ 전용, 30초 스파이크 역행 — 7/17)
            V11cStep(s, LastScore, inSess, minToClose, dk);      // V1.1c (MNQ 전용, V1.1+서킷 — 7/17)
            BbzStep(s, inSess, minToClose, dk);                  // BBZ (MNQ 전용, 볼린저 회귀 — 7/18)

            long now = NowMs();

            if (_pos != null)
            {
                double exitPx = _pos.Side == "LONG" ? s.BestBid : s.BestAsk;   // 상대호가 청산(보수)
                double pnl = (_pos.Side == "LONG" ? exitPx - _pos.Entry : _pos.Entry - exitPx) / _tick;
                _pos.UnrealizedTicks = pnl;

                string kind = null;
                // v5.27: V1 재해SL을 스프레드기반→ATR기반(TREND과 동일 배수 TrKAtr)으로 통일.
                //   스프레드기반(−3×spread)은 개장 광폭스프레드선 과대(7/6 08:45 −109틱→일손실상한 발동), MES 1틱스프레드선
                //   과소(3틱, 8/8 즉사)로 양방향 다 병리적. ATR×3.5는 regime-aware 백스톱(v5.20 known-good: 무손절 MDD−100 vs −57).
                //   근거=7/6 카운터팩추얼(9조합 전부 손절완화 유리). ⚠️shadow 단계 완화(실자본 위험0), 회원 지시 7/6. 며칠 누적후 워크포워드 재검증 필요.
                double disasterPt = _trKAtr * AtrPt(TrAtrWin);
                if (disasterPt <= 0) disasterPt = DisasterSpreadMult * _pos.EntrySpread;   // ATR 워밍업 미준비 시 폴백
                double disasterTicks = -disasterPt / _tick;
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
            if (EnforceDayCap && _dayCapHit) return;                      // 일손실 상한 — shadow 단계선 관측만(진입 안 막음, v5.26)
            if (now < _cooldownUntil) return;
            if (Math.Abs(score) < EnterTh) return;
            if (SpreadTooWide(s, "V1")) return;

            string side = score > 0 ? "LONG" : "SHORT";
            double entryPx = side == "LONG" ? s.BestAsk : s.BestBid;      // taker 진입(7/3 검증: 5분 보유가 비용 흡수)
            _pos = new PaperPosition { Side = side, Entry = entryPx, EntrySpread = s.Spread, OpenedMs = now, Score = score };
            // 국면분류·정확 카운터팩추얼용 진입 진단(2026-07-07): er(30분 EffRatio)/atr/재해손절폭(dstop,틱)/스프레드(틱). 순수 로깅, 결정로직·스키마 무변경.
            double atrPtE = AtrPt(TrAtrWin), erE = EffRatio(TrErWin);
            double dstopE = _trKAtr * atrPtE; if (dstopE <= 0) dstopE = DisasterSpreadMult * s.Spread;
            string diagE = $"er={erE:F2} atr={atrPtE:F3} dstop={dstopE / _tick:F0} spr={s.Spread / _tick:F1}";
            Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = "V1", Side = side, Kind = "ENTRY", Price = entryPx, PnlTicks = 0, Score = score, Reason = LastScoreBreakdown + " | " + diagE });
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
                _dayCapHit = true;   // 관측 플래그(도달시점 기록) — EnforceDayCap=false면 진입은 계속(shadow 누적 목적, v5.26)
                _log($"[signal] 🔴 일손실 상한 도달({_dayPnlTicks:F0}틱) — {(EnforceDayCap ? "당일 신규 진입 중지" : "관측만(shadow: 진입 계속)")}");
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
        private double _diagMom, _diagThrPt, _diagEr, _diagAtrPt;  // 진단용(v5.22) — 왜 진입 안 하는지 라이브 확인

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
            public int SigSide;      // 호가필터 대기 중인 신호 방향(0=대기 없음) — v5.23
            public long SigStartMs;  // 그 신호가 처음 만족된 시각(대기시간 상한 계산용) — v5.23
            public TrendMode(string name, string label, bool filter) { Name = name; BaseLabel = label; UseFilter = filter; }
        }
        private readonly TrendMode _trV1 = new TrendMode("TREND", "추세추종 v1(10분모멘텀)", false);
        private readonly TrendMode _trV2 = new TrendMode("TREND+OB", "추세+호가필터 v2", true);
        // V1-INV(2026-07-09, 회원 승인): V1 거울상(fade) shadow — 7/7·7/8 이틀 연속 신호역전=손실·fade>follow 재현,
        // 게이트 후보 전멸(per-entry 교차검증·day-level 84일 캘리브레이션 모두 무효) → 예측 대신 상시 실측.
        // 추세일 도래 시 "INV가 깨지는가"가 자동 판정됨(chop 한정 fade vs 신호 구조 결함 구분). 실주문 없음(룰8).
        private readonly TrendMode _v1Inv = new TrendMode("V1-INV", "V1 반전(fade) shadow", false);
        private long _invCooldownUntil;
        // V1.1/V1.2 (2026-07-11, 회원 승인 — V1 부검 기반 개선 실험체, MNQ 전용, 각각 한 가지만 변경):
        //   부검: V1 손실=역전거래 전부(−20,118 vs 비역전 +3,343), 역전은 진입피처로 예측불가, 역전 익는 중앙 153초.
        //   V1.1 = 보유 300→180초 + SL ATR×5.0 (그리드 3일 리플레이 3/3일 + 합 +1,596틱 — 역전이 익기 전 탈출)
        //   V1.2 = V1 동일 + REV 청산(보유 중 score가 진입 반대부호 |0.2|이상 → 즉시 청산. ⚠️0.2=재량값, 리플레이 검증불가라 라이브 shadow가 검증)
        private readonly TrendMode _v11 = new TrendMode("V1.1", "V1.1 3분보유/SL5x (MNQ)", false);
        private readonly TrendMode _v12 = new TrendMode("V1.2", "V1.2 역전즉시청산 (MNQ)", false);
        // ── BEST 합성(7/13 회원 지시 "수익 조건 적용하면서 보자") — 조건부 분석 화이트리스트를 실제 모드로 ──
        private readonly TrendMode _best = new TrendMode("BEST", "수익조건 합성(TREND 화이트리스트)", false);
        private readonly TrendMode _bestV1 = new TrendMode("BEST-V1", "V1+변동성밴드+독성시간차단 (MNQ)", false);
        private long _bestV1CooldownUntil;
        public const double BestV1AtrLo = 4.6, BestV1AtrHi = 7.6;   // MNQ V1 중간변동성만 양수(4/5일 +3,035틱, 7/13 조건분석)
        // ── GAP 모드(7/13 S1 리서치): 오버나이트 갭 지속 — 84일 지수 백테스트 갭방향 →10시 +24~35bp·승률 56~60%(t 1.2~1.5 미확정 → shadow 실측으로 판정) ──
        private readonly TrendMode _gap = new TrendMode("GAP", "오버나이트 갭 지속(1일1회, 미니)", false);
        private double _prevClose;          // 전일종가 = price − change (틱마다 갱신)
        private string _gapDoneDay = "";    // 1일 1회 가드
        public const double GapMinBp = 40.0;                          // |갭| 최소(84일: ≥40bp 승률 58~62%)
        public const int GapEntryFromMin = 526, GapEntryToMin = 555;  // 진입창 08:46~09:15 (분)
        public const int GapExitMin = 600;                            // 10:00 청산 (백테스트 최일관 구간)

        /// <summary>틱 참조값 공급(InstrumentPipeline.OnTick) — 전일종가 역산용.
        /// ⚠️7/14 버그 fix: KRX 미니의 change는 부호 없는 절대값(부호는 별도 구분필드)이라 price−change가 하락일에 틀림
        /// (첫 실전: 진짜 갭 −193bp를 +209bp로 오산해 LONG 진입). drate(등락률)는 양 시장 모두 부호 확인 → 이걸로 역산.</summary>
        public void OnTickRef(double price, double drate)
        {
            if (price > 0 && drate > -99 && drate < 99 && Math.Abs(drate) > 1e-9)
                _prevClose = price / (1 + drate / 100.0);
        }
        private long _v11CooldownUntil, _v12CooldownUntil;
        private long _v12RevArmedAt;   // V1.2r: 역전 지속성 타이머 시작점(0=미가동)
        public const int V11HoldMs = 180000;
        public const double V11KAtr = 5.0;
        public const double V12RevTh = 0.2;
        // V1.2r(7/13 부검): 즉시청산은 보유중앙 5초 노이즈 털림 442건/-1,281틱 실증 → 역전이 지속돼야만 청산.
        // 30s 근거 = 노이즈 청산 5s의 6배 · 역전 성숙 중앙 153s의 1/5 (모드당 1변경 원칙 — 임계 0.2는 유지)
        public const int V12RevPersistMs = 30000;
        // ── DFADE 모드(7/15 raw 틱 전수발굴 생존자 — "US 수급소진 페이드", 파라미터 동결): MNQ 전용.
        //   US(22:30~05:00) 중 |2분 공격체결 델타| ≥ 1455계약 → 역방향 진입, 300초 보유(TIME). SL 없음(발굴 스펙 재현성 우선 — EOD·무수신만 공유).
        //   발굴: 훈련 7/7~10 4/4일 + 검증 7/13~14 2/2일 = 6/6일 양수 +3,812틱, EV+25.2틱/건(비용 3.5틱 차감), t=1.98, 승률 52%.
        //   메커니즘: US 세션 역추세성(P(5분지속)=0.475) — 공격수급 극단(상위 3%)=소진, 수동 유동성이 되받아침.
        //   오프라인 트래커(rule_lab 🌊 df)와 동일 룰 병행 = 실호가 체결 vs 3.5틱 가정 비용의 실측 대조군.
        private readonly TrendMode _dfade = new TrendMode("DFADE", "US 수급소진 페이드", false);
        public const double DfDeltaThr = 1455;          // 2분 공격체결 불균형(계약) — 발굴 훈련 p97 절대값 동결(재산출 금지)
        public const int DfWinMs = 120000, DfHoldMs = 300000;
        // ── DFADE-X(7/17 발굴기 v2 최종 승자 — 1,020조합 중 유일 9/9일 양수, 홀드아웃 [+172,+1167]): MNQ 전용.
        //   DFADE + 호가 확인필터: 마이크로프라이스가 수급 반대로 >=0.30틱 기울었을 때만(수동벽이 저항 중) 페이드.
        //   벽이 밀리면(진짜 추세) 관망 — 원판의 추세의밤(7/15 −516) 약점 봉합(+172). 임계 1,422=발굴 훈련 p97 동결.
        private readonly TrendMode _dfx = new TrendMode("DFADE-X", "수급극단×호가저항 페이드", false);
        public const double DfxDeltaThr = 1422, DfxMdevMin = 0.30;
        // ── M30F(7/17 발굴기 v2, 완결본 정정 후 8일 +2,034·6/8일): 30초 가격 스파이크 >=47틱(훈련 p90 동결) 역행, 300s.
        private readonly TrendMode _m30 = new TrendMode("M30F", "30초 스파이크 역행", false);
        public const double M30Thr = 47.0;              // 틱
        private readonly List<double[]> _m30Px = new List<double[]>();   // [ms, mid] 1초 샘플 35s 링
        private long _m30LastMs;
        // ── BBZ(7/18 동물원 v3 전승자 — 10/10일 양수 +6,577틱, 파라미터 동결): US 중 1초가격이 10분(600s)
        //   이동평균 ±2.0σ 밴드 이탈 → 회귀 방향, 300초 보유. 적응형 밴드라 추세와 안 싸움(추세의밤 7/15 +786).
        //   DFADE-X와 상관 +0.17 — "페이드 듀오"의 반쪽. 링버퍼 1초 샘플 600개 + 러닝 합/제곱합(O(1)).
        private readonly TrendMode _bbz = new TrendMode("BBZ", "볼린저 2σ 회귀", false);
        public const double BbzTh = 2.0;
        public const int BbzWin = 600;
        private readonly List<double> _bbzPx = new List<double>();   // 1초 샘플(틱 단위 가격), 600개 링
        private double _bbzSum, _bbzSumSq;
        private long _bbzLastMs;
        // ── V1.1c(7/17 개조실험 유일 통과 — 4변형 중 양 주간 개선): V1.1 + US 게이트 + 5연패 서킷.
        //   라이브 사후검증 4일 +3,121 vs 원본 +1,048. 서킷 근거=6,100건 부검("생존 구조가 승패를 가름").
        //   ⚠️서킷 카운터는 재기동 시 리셋(복원 불가) — 재기동 잦은 날 서킷 지연 가능(관측 라벨).
        private readonly TrendMode _v11c = new TrendMode("V1.1c", "V1.1+5연패 서킷 (US)", false);
        public const int V11cLossStreakStop = 5;
        private long _v11cCooldownUntil;
        private int _v11cLossStreak;
        private string _v11cCircuitDay = "";
        private readonly List<double[]> _dfFlow = new List<double[]>();   // [ms, signedVol] 120s 링버퍼
        private double _dfDeltaSum;

        /// <summary>체결 공격방향 유입(InstrumentPipeline.OnTick) — DFADE 120s 수급델타. cgubun '+'=매수공격 / '-'=매도공격(FC9·OVC 공통).</summary>
        public void OnTickFlow(string cgubun, long cvolume)
        {
            if (cvolume <= 0 || string.IsNullOrEmpty(cgubun)) return;
            double sv = cgubun == "+" ? cvolume : (cgubun == "-" ? -cvolume : 0);
            if (sv == 0) return;
            long now = NowMs();
            _dfFlow.Add(new double[] { now, sv });
            _dfDeltaSum += sv;
            long cut = now - DfWinMs;
            int i = 0;
            while (i < _dfFlow.Count && _dfFlow[i][0] < cut) { _dfDeltaSum -= _dfFlow[i][1]; i++; }
            if (i > 0) _dfFlow.RemoveRange(0, i);
        }

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
            _diagMom = mom; _diagThrPt = thrPt; _diagEr = er; _diagAtrPt = atrPt;
            HandleTrend(_trV1, s, mom, thrPt, er, atrPt, inSess, minToClose, dk);
            HandleTrend(_trV2, s, mom, thrPt, er, atrPt, inSess, minToClose, dk);
            HandleTrend(_best, s, mom, thrPt, er, atrPt, inSess, minToClose, dk, BestTrendGate(s, er));   // BEST: 게이트 밖에선 진입만 차단(청산은 항상 처리)
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

        private void HandleTrend(TrendMode m, FeatureSnapshot s, double mom, double thrPt, double er, double atrPt, bool inSess, double minToClose, string dk, bool entryOK = true)
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
            if (!entryOK) return;                     // BEST류 화이트리스트 게이트 — 진입만 차단
            if (!inSess || minToClose <= NoEntryBeforeCloseMin) return;
            if (_stale || s.DanHo) return;
            if (Math.Abs(mom) < thrPt) return;        // 추세 약함
            if (er < _trErThr) return;                // 횡보
            if (SpreadTooWide(s, m.Name)) return;     // 스프레드 상한(2026-07-09)
            int side = mom > 0 ? 1 : -1;
            if (m.UseFilter)
            {
                // v5.23: 호가일치 대기시간 상한 — 7/6 실측(미니 TREND vs TREND+OB 7건 페어비교)에서
                // ObAgree가 즉시(0.4~1.2초) 맞을 땐 문제없었으나 오래(9.4~17.4초) 걸린 케이스는 매번 그 사이
                // 가격이 추세방향으로 더 가버려 v2가 v1보다 나쁜 가격에 진입 → 손실 확대(필터가 거른 거래는 0건,
                // 지연비용만 발생). 같은 방향 신호가 MaxObWaitMs 이상 미확인이면 그 신호는 포기(추격 금지).
                if (m.SigSide != side) { m.SigSide = side; m.SigStartMs = now; }
                if (!ObAgree(s, side))
                {
                    if (now - m.SigStartMs > MaxObWaitMs) m.SigSide = 0;   // 신호 소멸 — 다음 새 신호까지 재시도 안 함
                    return;
                }
            }
            string sideStr = side > 0 ? "LONG" : "SHORT";
            double entryPx = side > 0 ? s.BestAsk : s.BestBid;
            m.Pos = new PaperPosition { Side = sideStr, Entry = entryPx, EntrySpread = s.Spread, OpenedMs = now, Score = mom };
            m.SigSide = 0;
            Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = sideStr, Kind = "ENTRY", Price = entryPx, PnlTicks = 0, Score = mom, Reason = $"mom={mom:F2} er={er:F2} atr={atrPt:F3} stop={_trKAtr * atrPt / _tick:F0} spr={s.Spread / _tick:F1}" });
        }
        private const long MaxObWaitMs = 3000;

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

        /// <summary>진입 스프레드 상한 가드 — 초과 시 카운터 증가+60초 스로틀 로그 후 true(진입 스킵).
        /// 전 모드 공통 호출(V1/V1-INV/TREND류). 근거·수치는 MaxEntrySpreadTicks 주석 참조.</summary>
        private bool SpreadTooWide(FeatureSnapshot s, string mode)
        {
            if (s.Spread / _tick <= MaxEntrySpreadTicks) return false;
            _sprSkips++;
            long now = NowMs();
            if (now - _lastSprSkipLogMs >= 60000)
            {
                _lastSprSkipLogMs = now;
                _log($"[signal] {_instName} {mode} 진입스킵: 스프레드 {s.Spread / _tick:F0}틱 > 상한 {MaxEntrySpreadTicks}틱 (누적 {_sprSkips}회)");
            }
            return true;
        }

        /// <summary>V1-INV Step — V1과 완전 대칭(트리거 |s|≥EnterTh·5분보유·ATR재해SL·EOD·쿨다운), 방향만 반전.
        /// 청산 기록의 Score=청산시점 score(V1 CloseAt와 동일 규약) — 진입 vs 청산 부호로 역전분석 가능하게 유지.
        /// 성적은 TrendMode 컨테이너(_v1Inv)에 — GetState modes 자동 노출·RestoreTrendDay 복원 공용.</summary>
        private void V1InvStep(FeatureSnapshot s, double score, bool inSess, double minToClose, string dk)
        {
            var m = _v1Inv;
            if (m.DayKey != dk) { m.DayKey = dk; m.DayPnlTicks = 0; m.DayCommission = 0; }
            long now = NowMs();
            if (m.Pos != null)
            {
                double exitPx = m.Pos.Side == "LONG" ? s.BestBid : s.BestAsk;   // 상대호가 청산(보수) — V1 동일
                double pnl = (m.Pos.Side == "LONG" ? exitPx - m.Pos.Entry : m.Pos.Entry - exitPx) / _tick;
                m.Pos.UnrealizedTicks = pnl;
                double disasterPt = _trKAtr * AtrPt(TrAtrWin);
                if (disasterPt <= 0) disasterPt = DisasterSpreadMult * m.Pos.EntrySpread;   // ATR 워밍업 폴백 — V1 동일
                string kind = null;
                if (pnl <= -disasterPt / _tick) kind = "DISASTER";
                else if (now - m.Pos.OpenedMs >= HoldMs) kind = "TIME";
                else if (!inSess || minToClose <= ForceCloseBeforeCloseMin) kind = "EOD";
                if (kind == null) return;
                m.DayPnlTicks += pnl; m.Trades++; if (pnl > 0) m.Wins++;
                m.DayCommission += _commPct > 0 ? (m.Pos.Entry + exitPx) * _mult * _commPct : _commissionRt;
                Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = m.Pos.Side, Kind = kind, Price = exitPx, PnlTicks = pnl, Score = score, Reason = LastScoreBreakdown });
                _log($"[signal] V1-INV {m.Pos.Side} {kind} @{exitPx:F2} pnl={pnl:+0.0;-0.0}틱 (일누적 {m.DayPnlTicks:+0.0;-0.0}틱)");
                m.Pos = null;
                _invCooldownUntil = now + CooldownMs;
                return;
            }
            if (!inSess || minToClose <= NoEntryBeforeCloseMin) return;
            if (_stale || s.DanHo) return;
            if (now < _invCooldownUntil) return;
            if (Math.Abs(score) < EnterTh) return;
            if (SpreadTooWide(s, "V1-INV")) return;
            string side = score > 0 ? "SHORT" : "LONG";                        // ← V1의 정확한 거울상
            double entryPx = side == "LONG" ? s.BestAsk : s.BestBid;           // taker 진입(반전 방향 기준)
            m.Pos = new PaperPosition { Side = side, Entry = entryPx, EntrySpread = s.Spread, OpenedMs = now, Score = score };
            double atrPtI = AtrPt(TrAtrWin), erI = EffRatio(TrErWin);
            double dstopI = _trKAtr * atrPtI; if (dstopI <= 0) dstopI = DisasterSpreadMult * s.Spread;
            Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = side, Kind = "ENTRY", Price = entryPx, PnlTicks = 0, Score = score, Reason = LastScoreBreakdown + $" | er={erI:F2} atr={atrPtI:F3} dstop={dstopI / _tick:F0} spr={s.Spread / _tick:F1} [INV]" });
            _log($"[signal] V1-INV {side} 진입 @{entryPx:F2} score={score:F2} (반전)");
        }

        /// <summary>V1.1/V1.2 공용 Step (MNQ 전용) — V1과 동일 진입(방향·임계·가드), 청산만 변형(모드당 한 가지 변경=귀속 명확).
        /// V1.1: 보유 V11HoldMs(180s)·SL ATR×V11KAtr(5.0) / V1.2: V1 청산 + REV(score가 진입 반대부호 |V12RevTh| 도달 시 즉시청산).</summary>
        private void V1VarStep(TrendMode m, FeatureSnapshot s, double score, bool inSess, double minToClose, string dk)
        {
            if (!_instCode.StartsWith("MNQ")) return;   // MNQ 전용 — 미니는 그리드 16셀 전멸(부검 판정), 적용 안 함
            if (m.DayKey != dk) { m.DayKey = dk; m.DayPnlTicks = 0; m.DayCommission = 0; }
            long now = NowMs();
            bool is11 = ReferenceEquals(m, _v11);
            if (m.Pos != null)
            {
                double exitPx = m.Pos.Side == "LONG" ? s.BestBid : s.BestAsk;
                double pnl = (m.Pos.Side == "LONG" ? exitPx - m.Pos.Entry : m.Pos.Entry - exitPx) / _tick;
                m.Pos.UnrealizedTicks = pnl;
                double disasterPt = (is11 ? V11KAtr : _trKAtr) * AtrPt(TrAtrWin);
                if (disasterPt <= 0) disasterPt = DisasterSpreadMult * m.Pos.EntrySpread;
                int holdMs = is11 ? V11HoldMs : HoldMs;
                string kind = null;
                if (pnl <= -disasterPt / _tick) kind = "DISASTER";
                else if (!is11 && V12RevSustained(m, score, now)) kind = "REV";   // V1.2r: 역전이 V12RevPersistMs 지속 시에만 청산
                else if (now - m.Pos.OpenedMs >= holdMs) kind = "TIME";
                else if (!inSess || minToClose <= ForceCloseBeforeCloseMin) kind = "EOD";
                if (kind == null) return;
                m.DayPnlTicks += pnl; m.Trades++; if (pnl > 0) m.Wins++;
                m.DayCommission += _commPct > 0 ? (m.Pos.Entry + exitPx) * _mult * _commPct : _commissionRt;
                Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = m.Pos.Side, Kind = kind, Price = exitPx, PnlTicks = pnl, Score = score, Reason = LastScoreBreakdown });
                _log($"[signal] {m.Name} {m.Pos?.Side} {kind} @{exitPx:F2} pnl={pnl:+0.0;-0.0}틱");
                m.Pos = null;
                if (is11) _v11CooldownUntil = now + CooldownMs; else { _v12CooldownUntil = now + CooldownMs; _v12RevArmedAt = 0; }
                return;
            }
            if (!inSess || minToClose <= NoEntryBeforeCloseMin) return;
            if (_stale || s.DanHo) return;
            if (now < (is11 ? _v11CooldownUntil : _v12CooldownUntil)) return;
            if (Math.Abs(score) < EnterTh) return;
            if (SpreadTooWide(s, m.Name)) return;
            string side = score > 0 ? "LONG" : "SHORT";   // V1과 동일 방향(follow)
            double entryPx = side == "LONG" ? s.BestAsk : s.BestBid;
            m.Pos = new PaperPosition { Side = side, Entry = entryPx, EntrySpread = s.Spread, OpenedMs = now, Score = score };
            if (!is11) _v12RevArmedAt = 0;   // 새 포지션 = 지속성 타이머 초기화
            double atrPtV = AtrPt(TrAtrWin), erV = EffRatio(TrErWin);
            double dstopV = (is11 ? V11KAtr : _trKAtr) * atrPtV; if (dstopV <= 0) dstopV = DisasterSpreadMult * s.Spread;
            Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = side, Kind = "ENTRY", Price = entryPx, PnlTicks = 0, Score = score, Reason = LastScoreBreakdown + $" | er={erV:F2} atr={atrPtV:F3} dstop={dstopV / _tick:F0} spr={s.Spread / _tick:F1} [{m.Name}]" });
        }

        /// <summary>BEST(TREND 합성) 진입 게이트 — 7/13 조건부 분석 화이트리스트.
        /// 미니: KRX 주간 08:45~15:45만(4/4일 +2,779틱, 야간=4/4일 순수 독).
        /// MNQ: 아시아 08:45~15:45(4/4일) + 심야 01~05h(3/3일), 그리고 er>=0.40(5/5일 일관).</summary>
        private bool BestTrendGate(FeatureSnapshot s, double er)
        {
            int hm;
            try { hm = int.Parse(s.Stamp.Substring(0, 2)) * 60 + int.Parse(s.Stamp.Substring(3, 2)); }
            catch { return false; }
            bool day = hm >= 525 && hm <= 945;
            if (!_instCode.StartsWith("MNQ")) return day;
            bool night = hm >= 60 && hm < 300;
            return (day || night) && er >= 0.40;
        }

        /// <summary>BEST-V1(MNQ 전용): V1과 동일 진입·청산(5분보유/ATR×3.5 재해/EOD)에
        /// 진입 게이트만 추가 — 변동성 밴드(atr 4.6~7.6pt, 4/5일 +3,035틱) + 독성시간(22·23·01h, 4/4일) 차단.</summary>
        private void BestV1Step(FeatureSnapshot s, double score, bool inSess, double minToClose, string dk)
        {
            if (!_instCode.StartsWith("MNQ")) return;
            var m = _bestV1;
            if (m.DayKey != dk) { m.DayKey = dk; m.DayPnlTicks = 0; m.DayCommission = 0; }
            long now = NowMs();
            if (m.Pos != null)
            {
                double exitPx = m.Pos.Side == "LONG" ? s.BestBid : s.BestAsk;
                double pnl = (m.Pos.Side == "LONG" ? exitPx - m.Pos.Entry : m.Pos.Entry - exitPx) / _tick;
                m.Pos.UnrealizedTicks = pnl;
                double disasterPt = _trKAtr * AtrPt(TrAtrWin);
                if (disasterPt <= 0) disasterPt = DisasterSpreadMult * m.Pos.EntrySpread;
                string kind = null;
                if (pnl <= -disasterPt / _tick) kind = "DISASTER";
                else if (now - m.Pos.OpenedMs >= HoldMs) kind = "TIME";
                else if (!inSess || minToClose <= ForceCloseBeforeCloseMin) kind = "EOD";
                if (kind == null) return;
                m.DayPnlTicks += pnl; m.Trades++; if (pnl > 0) m.Wins++;
                m.DayCommission += _commPct > 0 ? (m.Pos.Entry + exitPx) * _mult * _commPct : _commissionRt;
                Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = m.Pos.Side, Kind = kind, Price = exitPx, PnlTicks = pnl, Score = score, Reason = LastScoreBreakdown });
                _log($"[signal] {m.Name} {m.Pos?.Side} {kind} @{exitPx:F2} pnl={pnl:+0.0;-0.0}틱");
                m.Pos = null; _bestV1CooldownUntil = now + CooldownMs;
                return;
            }
            if (!inSess || minToClose <= NoEntryBeforeCloseMin) return;
            if (_stale || s.DanHo) return;
            if (now < _bestV1CooldownUntil) return;
            if (Math.Abs(score) < EnterTh) return;
            if (SpreadTooWide(s, m.Name)) return;
            double atrPtV = AtrPt(TrAtrWin);
            int hh;
            try { hh = int.Parse(s.Stamp.Substring(0, 2)); } catch { return; }
            if (hh == 22 || hh == 23 || hh == 1) return;                    // 독성시간 차단
            if (atrPtV < BestV1AtrLo || atrPtV >= BestV1AtrHi) return;      // 변동성 밴드 밖 차단
            string side = score > 0 ? "LONG" : "SHORT";
            double entryPx = side == "LONG" ? s.BestAsk : s.BestBid;
            m.Pos = new PaperPosition { Side = side, Entry = entryPx, EntrySpread = s.Spread, OpenedMs = now, Score = score };
            double erV = EffRatio(TrErWin);
            double dstopV = _trKAtr * atrPtV; if (dstopV <= 0) dstopV = DisasterSpreadMult * s.Spread;
            Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = side, Kind = "ENTRY", Price = entryPx, PnlTicks = 0, Score = score, Reason = LastScoreBreakdown + $" | er={erV:F2} atr={atrPtV:F3} dstop={dstopV / _tick:F0} spr={s.Spread / _tick:F1} [BEST-V1]" });
        }

        /// <summary>GAP(미니 전용): 개장 직후(08:46~09:15) 오버나이트 갭 방향으로 1일 1회 진입, 10:00 청산.
        /// 근거=84일 지수 백테스트(갭방향 →10시 지속이 전 컷에서 양수·하락갭이 더 강함). 재해SL은 ATR×3.5 안전망 동일.</summary>
        private void GapStep(FeatureSnapshot s, bool inSess, double minToClose, string dk)
        {
            if (_instCode.StartsWith("MNQ")) return;   // KRX 주간 갭 전용
            var m = _gap;
            if (m.DayKey != dk) { m.DayKey = dk; m.DayPnlTicks = 0; m.DayCommission = 0; }
            long now = NowMs();
            int hm;
            try { hm = int.Parse(s.Stamp.Substring(0, 2)) * 60 + int.Parse(s.Stamp.Substring(3, 2)); } catch { return; }
            if (m.Pos != null)
            {
                double exitPx = m.Pos.Side == "LONG" ? s.BestBid : s.BestAsk;
                double pnl = (m.Pos.Side == "LONG" ? exitPx - m.Pos.Entry : m.Pos.Entry - exitPx) / _tick;
                m.Pos.UnrealizedTicks = pnl;
                double disasterPt = _trKAtr * AtrPt(TrAtrWin);
                if (disasterPt <= 0) disasterPt = DisasterSpreadMult * m.Pos.EntrySpread;
                string kind = null;
                if (pnl <= -disasterPt / _tick) kind = "DISASTER";
                else if (hm >= GapExitMin) kind = "TIME";
                else if (!inSess || minToClose <= ForceCloseBeforeCloseMin) kind = "EOD";
                if (kind == null) return;
                m.DayPnlTicks += pnl; m.Trades++; if (pnl > 0) m.Wins++;
                m.DayCommission += _commPct > 0 ? (m.Pos.Entry + exitPx) * _mult * _commPct : _commissionRt;
                Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = m.Pos.Side, Kind = kind, Price = exitPx, PnlTicks = pnl, Score = 0, Reason = "" });
                _log($"[signal] {m.Name} {m.Pos?.Side} {kind} @{exitPx:F2} pnl={pnl:+0.0;-0.0}틱");
                m.Pos = null;
                return;
            }
            if (_gapDoneDay == dk) return;                          // 1일 1회
            if (hm < GapEntryFromMin || hm > GapEntryToMin) return; // 진입창 밖
            if (!inSess || _stale || s.DanHo) return;
            if (_prevClose <= 0) return;
            if (SpreadTooWide(s, m.Name)) return;
            double mid = (s.BestBid + s.BestAsk) / 2.0;
            double gapBp = (mid / _prevClose - 1) * 1e4;
            if (Math.Abs(gapBp) < GapMinBp) return;                 // 갭 미달 — 창 안에서 계속 재평가
            string side = gapBp > 0 ? "LONG" : "SHORT";
            double entryPx = side == "LONG" ? s.BestAsk : s.BestBid;
            m.Pos = new PaperPosition { Side = side, Entry = entryPx, EntrySpread = s.Spread, OpenedMs = now, Score = gapBp };
            _gapDoneDay = dk;
            Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = side, Kind = "ENTRY", Price = entryPx, PnlTicks = 0, Score = gapBp, Reason = $"gap={gapBp:F1}bp prevC={_prevClose:F2} spr={s.Spread / _tick:F1} [GAP]" });
        }

        /// <summary>DFADE(MNQ 전용): US 세션(22:30~05:00) 수급소진 페이드 — |2분 공격체결 델타|≥DfDeltaThr → 역방향, 300s 보유.
        /// 발굴 스펙 동결(SL·쿨다운 없음 — 재현성 우선). EOD·마감가드·스프레드상한·무수신 진입금지는 전 모드 공통 안전망 공유.</summary>
        private void DfStep(FeatureSnapshot s, bool inSess, double minToClose, string dk)
        {
            if (!_instCode.StartsWith("MNQ")) return;
            var m = _dfade;
            if (m.DayKey != dk) { m.DayKey = dk; m.DayPnlTicks = 0; m.DayCommission = 0; }
            long now = NowMs();
            if (m.Pos != null)
            {
                double exitPx = m.Pos.Side == "LONG" ? s.BestBid : s.BestAsk;
                double pnl = (m.Pos.Side == "LONG" ? exitPx - m.Pos.Entry : m.Pos.Entry - exitPx) / _tick;
                m.Pos.UnrealizedTicks = pnl;
                string kind = null;
                if (now - m.Pos.OpenedMs >= DfHoldMs) kind = "TIME";
                else if (!inSess || minToClose <= ForceCloseBeforeCloseMin) kind = "EOD";
                if (kind == null) return;
                m.DayPnlTicks += pnl; m.Trades++; if (pnl > 0) m.Wins++;
                m.DayCommission += _commPct > 0 ? (m.Pos.Entry + exitPx) * _mult * _commPct : _commissionRt;
                Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = m.Pos.Side, Kind = kind, Price = exitPx, PnlTicks = pnl, Score = m.Pos.Score, Reason = "" });
                _log($"[signal] {m.Name} {m.Pos?.Side} {kind} @{exitPx:F2} pnl={pnl:+0.0;-0.0}틱 (일누적 {m.DayPnlTicks:+0.0;-0.0}틱)");
                m.Pos = null;
                return;
            }
            int hm;
            try { hm = int.Parse(s.Stamp.Substring(0, 2)) * 60 + int.Parse(s.Stamp.Substring(3, 2)); } catch { return; }
            if (!(hm >= 1350 || hm < 300)) return;                 // US 세션 게이트 22:30(1350)~05:00(300)
            if (!inSess || minToClose <= NoEntryBeforeCloseMin) return;
            if (_stale || s.DanHo) return;
            if (Math.Abs(_dfDeltaSum) < DfDeltaThr) return;
            if (SpreadTooWide(s, m.Name)) return;
            string side = _dfDeltaSum > 0 ? "SHORT" : "LONG";      // 페이드 — 공격수급의 반대
            double entryPx = side == "LONG" ? s.BestAsk : s.BestBid;
            m.Pos = new PaperPosition { Side = side, Entry = entryPx, EntrySpread = s.Spread, OpenedMs = now, Score = _dfDeltaSum };
            Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = side, Kind = "ENTRY", Price = entryPx, PnlTicks = 0, Score = _dfDeltaSum, Reason = $"d120={_dfDeltaSum:F0}계약 spr={s.Spread / _tick:F1} [DFADE]" });
            _log($"[signal] DFADE {side} 진입 @{entryPx:F2} d120={_dfDeltaSum:F0}계약");
        }

        /// <summary>DFADE-X(MNQ 전용): DFADE + 호가저항 확인 — 마이크로프라이스가 수급 반대로 기울었을 때만 페이드(7/17).</summary>
        private void DfxStep(FeatureSnapshot s, bool inSess, double minToClose, string dk)
        {
            if (!_instCode.StartsWith("MNQ")) return;
            var m = _dfx;
            if (m.DayKey != dk) { m.DayKey = dk; m.DayPnlTicks = 0; m.DayCommission = 0; }
            long now = NowMs();
            if (m.Pos != null)
            {
                double exitPx = m.Pos.Side == "LONG" ? s.BestBid : s.BestAsk;
                double pnl = (m.Pos.Side == "LONG" ? exitPx - m.Pos.Entry : m.Pos.Entry - exitPx) / _tick;
                m.Pos.UnrealizedTicks = pnl;
                string kind = null;
                if (now - m.Pos.OpenedMs >= DfHoldMs) kind = "TIME";
                else if (!inSess || minToClose <= ForceCloseBeforeCloseMin) kind = "EOD";
                if (kind == null) return;
                m.DayPnlTicks += pnl; m.Trades++; if (pnl > 0) m.Wins++;
                m.DayCommission += _commPct > 0 ? (m.Pos.Entry + exitPx) * _mult * _commPct : _commissionRt;
                Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = m.Pos.Side, Kind = kind, Price = exitPx, PnlTicks = pnl, Score = m.Pos.Score, Reason = "" });
                _log($"[signal] {m.Name} {m.Pos?.Side} {kind} @{exitPx:F2} pnl={pnl:+0.0;-0.0}틱 (일누적 {m.DayPnlTicks:+0.0;-0.0}틱)");
                m.Pos = null;
                return;
            }
            int hm;
            try { hm = int.Parse(s.Stamp.Substring(0, 2)) * 60 + int.Parse(s.Stamp.Substring(3, 2)); } catch { return; }
            if (!(hm >= 1350 || hm < 300)) return;
            if (!inSess || minToClose <= NoEntryBeforeCloseMin) return;
            if (_stale || s.DanHo) return;
            if (Math.Abs(_dfDeltaSum) < DfxDeltaThr) return;
            if (Math.Abs(s.MicroDevTicks) < DfxMdevMin) return;
            if ((s.MicroDevTicks > 0) == (_dfDeltaSum > 0)) return;   // 호가벽이 수급과 같은 방향(밀림) = 진짜 추세 → 관망
            if (SpreadTooWide(s, m.Name)) return;
            string side = _dfDeltaSum > 0 ? "SHORT" : "LONG";
            double entryPx = side == "LONG" ? s.BestAsk : s.BestBid;
            m.Pos = new PaperPosition { Side = side, Entry = entryPx, EntrySpread = s.Spread, OpenedMs = now, Score = _dfDeltaSum };
            Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = side, Kind = "ENTRY", Price = entryPx, PnlTicks = 0, Score = _dfDeltaSum, Reason = $"d120={_dfDeltaSum:F0} mdev={s.MicroDevTicks:F2} spr={s.Spread / _tick:F1} [DFX]" });
            _log($"[signal] DFADE-X {side} 진입 @{entryPx:F2} d120={_dfDeltaSum:F0} mdev={s.MicroDevTicks:F2}");
        }

        /// <summary>M30F(MNQ 전용): |30초 가격이동| >= 47틱 → 역행, 300초 보유(7/17 발굴기 v2).</summary>
        private void M30Step(FeatureSnapshot s, bool inSess, double minToClose, string dk)
        {
            if (!_instCode.StartsWith("MNQ")) return;
            var m = _m30;
            if (m.DayKey != dk) { m.DayKey = dk; m.DayPnlTicks = 0; m.DayCommission = 0; }
            long now = NowMs();
            double mid = (s.BestBid + s.BestAsk) / 2.0;
            if (now - _m30LastMs >= 1000)   // 1초 샘플, 35초 링
            {
                _m30Px.Add(new double[] { now, mid });
                _m30LastMs = now;
                while (_m30Px.Count > 0 && _m30Px[0][0] < now - 35000) _m30Px.RemoveAt(0);
            }
            if (m.Pos != null)
            {
                double exitPx = m.Pos.Side == "LONG" ? s.BestBid : s.BestAsk;
                double pnl = (m.Pos.Side == "LONG" ? exitPx - m.Pos.Entry : m.Pos.Entry - exitPx) / _tick;
                m.Pos.UnrealizedTicks = pnl;
                string kind = null;
                if (now - m.Pos.OpenedMs >= DfHoldMs) kind = "TIME";
                else if (!inSess || minToClose <= ForceCloseBeforeCloseMin) kind = "EOD";
                if (kind == null) return;
                m.DayPnlTicks += pnl; m.Trades++; if (pnl > 0) m.Wins++;
                m.DayCommission += _commPct > 0 ? (m.Pos.Entry + exitPx) * _mult * _commPct : _commissionRt;
                Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = m.Pos.Side, Kind = kind, Price = exitPx, PnlTicks = pnl, Score = m.Pos.Score, Reason = "" });
                _log($"[signal] {m.Name} {m.Pos?.Side} {kind} @{exitPx:F2} pnl={pnl:+0.0;-0.0}틱");
                m.Pos = null;
                return;
            }
            int hm;
            try { hm = int.Parse(s.Stamp.Substring(0, 2)) * 60 + int.Parse(s.Stamp.Substring(3, 2)); } catch { return; }
            if (!(hm >= 1350 || hm < 300)) return;
            if (!inSess || minToClose <= NoEntryBeforeCloseMin) return;
            if (_stale || s.DanHo) return;
            double past = double.NaN;
            for (int i = 0; i < _m30Px.Count; i++) { if (_m30Px[i][0] <= now - 30000) past = _m30Px[i][1]; else break; }
            if (double.IsNaN(past)) return;
            double mom30 = (mid - past) / _tick;
            if (Math.Abs(mom30) < M30Thr) return;
            if (SpreadTooWide(s, m.Name)) return;
            string side = mom30 > 0 ? "SHORT" : "LONG";
            double entryPx = side == "LONG" ? s.BestAsk : s.BestBid;
            m.Pos = new PaperPosition { Side = side, Entry = entryPx, EntrySpread = s.Spread, OpenedMs = now, Score = mom30 };
            Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = side, Kind = "ENTRY", Price = entryPx, PnlTicks = 0, Score = mom30, Reason = $"mom30={mom30:F0}틱 spr={s.Spread / _tick:F1} [M30F]" });
        }

        /// <summary>V1.1c(MNQ 전용): V1.1 동일(180s/SL ATR×5) + US 게이트 + 5연패 서킷(그날 밤 신규 중지, 7/17).</summary>
        private void V11cStep(FeatureSnapshot s, double score, bool inSess, double minToClose, string dk)
        {
            if (!_instCode.StartsWith("MNQ")) return;
            var m = _v11c;
            if (m.DayKey != dk) { m.DayKey = dk; m.DayPnlTicks = 0; m.DayCommission = 0; _v11cLossStreak = 0; if (_v11cCircuitDay != dk) _v11cCircuitDay = ""; }
            long now = NowMs();
            if (m.Pos != null)
            {
                double exitPx = m.Pos.Side == "LONG" ? s.BestBid : s.BestAsk;
                double pnl = (m.Pos.Side == "LONG" ? exitPx - m.Pos.Entry : m.Pos.Entry - exitPx) / _tick;
                m.Pos.UnrealizedTicks = pnl;
                double disasterPt = V11KAtr * AtrPt(TrAtrWin);
                if (disasterPt <= 0) disasterPt = DisasterSpreadMult * m.Pos.EntrySpread;
                string kind = null;
                if (pnl <= -disasterPt / _tick) kind = "DISASTER";
                else if (now - m.Pos.OpenedMs >= V11HoldMs) kind = "TIME";
                else if (!inSess || minToClose <= ForceCloseBeforeCloseMin) kind = "EOD";
                if (kind == null) return;
                m.DayPnlTicks += pnl; m.Trades++; if (pnl > 0) m.Wins++;
                m.DayCommission += _commPct > 0 ? (m.Pos.Entry + exitPx) * _mult * _commPct : _commissionRt;
                Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = m.Pos.Side, Kind = kind, Price = exitPx, PnlTicks = pnl, Score = score, Reason = LastScoreBreakdown });
                if (pnl <= 0) { _v11cLossStreak++; if (_v11cLossStreak >= V11cLossStreakStop && _v11cCircuitDay != dk) { _v11cCircuitDay = dk; _log($"[signal] V1.1c 🔌 5연패 서킷 발동 — {dk} 신규 진입 중지"); } }
                else _v11cLossStreak = 0;
                _log($"[signal] {m.Name} {m.Pos?.Side} {kind} @{exitPx:F2} pnl={pnl:+0.0;-0.0}틱 (연패 {_v11cLossStreak})");
                m.Pos = null;
                _v11cCooldownUntil = now + CooldownMs;
                return;
            }
            int hm;
            try { hm = int.Parse(s.Stamp.Substring(0, 2)) * 60 + int.Parse(s.Stamp.Substring(3, 2)); } catch { return; }
            if (!(hm >= 1350 || hm < 300)) return;                 // US 게이트
            if (_v11cCircuitDay == dk) return;                     // 서킷 발동일 — 신규 중지
            if (!inSess || minToClose <= NoEntryBeforeCloseMin) return;
            if (_stale || s.DanHo) return;
            if (now < _v11cCooldownUntil) return;
            if (Math.Abs(score) < EnterTh) return;
            if (SpreadTooWide(s, m.Name)) return;
            string side = score > 0 ? "LONG" : "SHORT";
            double entryPx = side == "LONG" ? s.BestAsk : s.BestBid;
            m.Pos = new PaperPosition { Side = side, Entry = entryPx, EntrySpread = s.Spread, OpenedMs = now, Score = score };
            double atrPtC = AtrPt(TrAtrWin), erC = EffRatio(TrErWin);
            double dstopC = V11KAtr * atrPtC; if (dstopC <= 0) dstopC = DisasterSpreadMult * s.Spread;
            Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = side, Kind = "ENTRY", Price = entryPx, PnlTicks = 0, Score = score, Reason = LastScoreBreakdown + $" | er={erC:F2} atr={atrPtC:F3} dstop={dstopC / _tick:F0} spr={s.Spread / _tick:F1} streak={_v11cLossStreak} [V1.1c]" });
        }

        /// <summary>BBZ(MNQ 전용): 1초가격이 10분(600s) 이동평균 ±2.0σ 이탈 → 회귀, 300초 보유(7/18 동물원 v3).</summary>
        private void BbzStep(FeatureSnapshot s, bool inSess, double minToClose, string dk)
        {
            if (!_instCode.StartsWith("MNQ")) return;
            var m = _bbz;
            if (m.DayKey != dk) { m.DayKey = dk; m.DayPnlTicks = 0; m.DayCommission = 0; }
            long now = NowMs();
            double midTicks = (s.BestBid + s.BestAsk) / 2.0 / _tick;
            if (now - _bbzLastMs >= 1000)
            {
                _bbzLastMs = now;
                _bbzPx.Add(midTicks); _bbzSum += midTicks; _bbzSumSq += midTicks * midTicks;
                if (_bbzPx.Count > BbzWin) { double o = _bbzPx[0]; _bbzPx.RemoveAt(0); _bbzSum -= o; _bbzSumSq -= o * o; }
            }
            if (m.Pos != null)
            {
                double exitPx = m.Pos.Side == "LONG" ? s.BestBid : s.BestAsk;
                double pnl = (m.Pos.Side == "LONG" ? exitPx - m.Pos.Entry : m.Pos.Entry - exitPx) / _tick;
                m.Pos.UnrealizedTicks = pnl;
                string kind = null;
                if (now - m.Pos.OpenedMs >= DfHoldMs) kind = "TIME";
                else if (!inSess || minToClose <= ForceCloseBeforeCloseMin) kind = "EOD";
                if (kind == null) return;
                m.DayPnlTicks += pnl; m.Trades++; if (pnl > 0) m.Wins++;
                m.DayCommission += _commPct > 0 ? (m.Pos.Entry + exitPx) * _mult * _commPct : _commissionRt;
                Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = m.Pos.Side, Kind = kind, Price = exitPx, PnlTicks = pnl, Score = m.Pos.Score, Reason = "" });
                _log($"[signal] {m.Name} {m.Pos?.Side} {kind} @{exitPx:F2} pnl={pnl:+0.0;-0.0}틱 (일누적 {m.DayPnlTicks:+0.0;-0.0}틱)");
                m.Pos = null;
                return;
            }
            int hm;
            try { hm = int.Parse(s.Stamp.Substring(0, 2)) * 60 + int.Parse(s.Stamp.Substring(3, 2)); } catch { return; }
            if (!(hm >= 1350 || hm < 300)) return;                 // US 게이트 22:30~05:00
            if (!inSess || minToClose <= NoEntryBeforeCloseMin) return;
            if (_stale || s.DanHo) return;
            if (_bbzPx.Count < BbzWin) return;                     // 10분 워밍업
            double mu = _bbzSum / BbzWin;
            double varr = Math.Max(_bbzSumSq / BbzWin - mu * mu, 1e-9);
            double z = (midTicks - mu) / Math.Sqrt(varr);
            if (Math.Abs(z) < BbzTh) return;
            if (SpreadTooWide(s, m.Name)) return;
            string side = z > 0 ? "SHORT" : "LONG";                // 회귀
            double entryPx = side == "LONG" ? s.BestAsk : s.BestBid;
            m.Pos = new PaperPosition { Side = side, Entry = entryPx, EntrySpread = s.Spread, OpenedMs = now, Score = z };
            Record(new PaperFill { Code = _instCode, Time = s.Stamp, Mode = m.Name, Side = side, Kind = "ENTRY", Price = entryPx, PnlTicks = 0, Score = z, Reason = $"bbz={z:F2} spr={s.Spread / _tick:F1} [BBZ]" });
            _log($"[signal] BBZ {side} 진입 @{entryPx:F2} z={z:F2}");
        }

        /// <summary>V1.2r 역전 지속성 판정: 반대부호 |V12RevTh| 상태가 V12RevPersistMs 연속 유지돼야 true.
        /// 반대신호가 끊기면 타이머 리셋 — 순간 노이즈(중앙 5s 실측)로는 절대 발화하지 않음.</summary>
        private bool V12RevSustained(TrendMode m, double score, long now)
        {
            bool ctr = m.Pos.Side == "LONG" ? score <= -V12RevTh : score >= V12RevTh;
            if (!ctr) { _v12RevArmedAt = 0; return false; }
            if (_v12RevArmedAt == 0) { _v12RevArmedAt = now; return false; }
            return now - _v12RevArmedAt >= V12RevPersistMs;
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
            modes.Add(TrendModeState(_v1Inv));  // V1 반전(fade) shadow — V1 카드 바로 옆에서 대조
            if (_instCode.StartsWith("MNQ")) { modes.Add(TrendModeState(_v11)); modes.Add(TrendModeState(_v12)); }   // V1.1/V1.2 (MNQ 전용)
            modes.Add(TrendModeState(_trV1));   // 추세추종 v1 (실시간 shadow)
            modes.Add(TrendModeState(_trV2));   // 추세+호가필터 v2
            modes.Add(TrendModeState(_best));   // BEST: 수익조건 합성(7/13)
            if (_instCode.StartsWith("MNQ")) { modes.Add(TrendModeState(_bestV1)); modes.Add(TrendModeState(_dfade)); modes.Add(TrendModeState(_dfx)); modes.Add(TrendModeState(_m30)); modes.Add(TrendModeState(_v11c)); modes.Add(TrendModeState(_bbz)); }   // DFADE/DFADE-X/M30F/V1.1c/BBZ (7/15~18)
            else modes.Add(TrendModeState(_gap));   // GAP: 미니 전용(7/13 S1)
            var recent = new List<PaperFill>();
            int start = Math.Max(0, _fills.Count - 30);
            for (int i = _fills.Count - 1; i >= start; i--) recent.Add(_fills[i]);
            return new
            {
                score = LastScore,
                breakdown = LastScoreBreakdown,
                paramsLabel = $"⚠️ v1({_instName}, shadow): |s|≥{EnterTh} 진입 → 5분 보유 / 재해SL −ATR×{_trKAtr}(v5.27 통일) / 마감{ForceCloseBeforeCloseMin}분전 청산 / 일손실 {DailyLossCapTicks}틱({(EnforceDayCap ? "중지" : "관측만")}) — 단일일 검증(룰8)",
                modes,
                trendDiag = new
                {
                    mom = _diagMom, thrPt = _diagThrPt, er = _diagEr, atrPt = _diagAtrPt,
                    passMom = Math.Abs(_diagMom) >= _diagThrPt, passEr = _diagEr >= _trErThr,
                    trThrFrac = _trThrFrac, trErThr = _trErThr, midsCount = _mids.Count
                },
                fills = recent.ConvertAll(f => (object)new
                {
                    time = f.Time, mode = f.Mode, side = f.Side, kind = f.Kind,
                    price = f.Price, pnlTicks = f.PnlTicks, score = f.Score
                }),
                spreadGuard = new
                {
                    maxTicks = MaxEntrySpreadTicks,
                    skips = _sprSkips,
                    note = "진입 스프레드>상한 스킵(2026-07-09) — 실측: 미니 >20틱 269건 −15,552틱 회피 근거"
                },
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
