using System;
using System.Collections.Generic;
using System.Diagnostics;
using LS.Futures.Models;

namespace LS.Futures
{
    /// <summary>피처 스냅샷 — STA 스레드가 만들고 참조 교체(volatile). 읽기 스레드는 복사 없이 참조만.</summary>
    public sealed class FeatureSnapshot
    {
        public string Stamp;                 // HH:mm:ss.fff (수신 벽시계)
        public double Ofi1s, Ofi5s, Ofi30s;  // 레벨1 OFI 윈도우 합 (Cont)
        public double Ofi5sZ;                // 5초 OFI z-score (최근 5분 표본 대비)
        public double QiL1, QiW5;            // queue imbalance (1호가 / 5단 거리가중)
        public double Mid, MicroPx, MicroDevTicks;  // micro-price − mid (틱 단위)
        public double Spread, Urgency;       // urgency = spread × |QiW5| (Optiver market_urgency 변형)
        public double Rv10, Rv10Z;           // 10초 실현변동성 + z
        public bool GapFlag;                 // 변동성 갭 이벤트 (회원 가설의 조작적 정의)
        public double Kasis, BasisZ;         // FC9 내장 괴리율 + z
        public bool Bear;                    // 선물 등락률 ≤ −1% (룰 8 레짐 게이트 — VKOSPI 미수신이라 근사)
        public bool DanHo;                   // 단일가호가(동시호가) — 진입 금지 구간
        public double BestBid, BestAsk;
        public long TotBidRem, TotOfferRem;
    }

    /// <summary>
    /// 스트리밍 피처 계산 (R6 §층1 → 코드). STA 이벤트 스레드에서만 호출 — lock 불필요.
    /// 전 계산 O(윈도우 증분). z-score는 1초 샘플링 deque(5~10분) 기반.
    /// ⚠️ 임계값/윈도우는 리서치 기반 초기값 — KOSPI200 미검증(룰 8), Phase 1 감쇠곡선 후 캘리브레이션 대상.
    /// </summary>
    public sealed class FeatureEngine
    {
        public readonly double Tick;         // 호가단위 — 종목별 주입(미니 0.02 / MES·MNQ 0.25). 다중종목 v5.
        public FeatureEngine(double tickSize) { Tick = tickSize; }

        // ── gap_flag 조작적 정의 초기값 (미검증 — 대시보드 표기용 public) ──
        public const double GapRvZ = 2.0;        // 10초 RV z ≥ 2
        public const double GapSpreadMin = 0.10; // 스프레드 ≥ 2틱
        public const double GapRemRatio = 0.7;   // 총잔량 < 60초 평균의 70%
        public const int GapHoldMs = 5000;       // 발동 후 5초 유지(모드 B 진입 창)

        private FH9Data _prevDepth;
        private volatile FeatureSnapshot _snap = new FeatureSnapshot();
        public FeatureSnapshot Snapshot { get { return _snap; } }

        // OFI 윈도우 (이벤트 큐 + 러닝 합)
        private readonly WindowSum _ofi1 = new WindowSum(1000);
        private readonly WindowSum _ofi5 = new WindowSum(5000);
        private readonly WindowSum _ofi30 = new WindowSum(30000);

        // 10초 RV: mid 로그수익률 제곱 합
        private readonly WindowSum _rv10 = new WindowSum(10000);
        private double _prevMid;

        // 1초 샘플 히스토리 (z-score 기반) — 5분/10분
        private readonly RollingStats _ofi5Hist = new RollingStats(300);
        private readonly RollingStats _rvHist = new RollingStats(300);
        private readonly RollingStats _basisHist = new RollingStats(600);
        private readonly RollingStats _totRemHist = new RollingStats(60);
        private long _lastSampleMs;

        private long _gapSinceMs;            // gap 최초 발동 시각(0=미발동)
        private decimal _lastKasis;
        private bool _bear;

        private static long NowMs() { return Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000); }

        public void OnTick(FC9Data t)
        {
            _lastKasis = t.Kasis;
            _bear = t.Drate <= -1.0m;   // 룰 8: 약세 레짐 근사(선물 등락률). VKOSPI 피드 확보 시 교체.
        }

        public void OnDepth(FH9Data d)
        {
            long now = NowMs();
            var s = new FeatureSnapshot();
            s.Stamp = d.ReceivedAt.ToString("HH:mm:ss.fff");

            double bid = (double)d.BidHo[0], ask = (double)d.OfferHo[0];
            double bq = d.BidRem[0], aq = d.OfferRem[0];
            s.BestBid = bid; s.BestAsk = ask;
            s.TotBidRem = d.TotBidRem; s.TotOfferRem = d.TotOfferRem;
            s.DanHo = d.DanHoChk == "1";

            if (bid <= 0 || ask <= 0) { _prevDepth = d; return; }   // 단일가/이상 호가 스킵

            // ── OFI (Cont level-1): e = ΔBid기여 − ΔAsk기여 ──
            if (_prevDepth != null && _prevDepth.BidHo[0] > 0)
            {
                double pb = (double)_prevDepth.BidHo[0], pa = (double)_prevDepth.OfferHo[0];
                double pbq = _prevDepth.BidRem[0], paq = _prevDepth.OfferRem[0];
                double e = 0;
                if (bid > pb) e += bq; else if (bid == pb) e += bq - pbq; else e -= pbq;
                if (ask < pa) e -= aq; else if (ask == pa) e -= aq - paq; else e += paq;
                _ofi1.Add(now, e); _ofi5.Add(now, e); _ofi30.Add(now, e);
            }
            s.Ofi1s = _ofi1.Sum(now); s.Ofi5s = _ofi5.Sum(now); s.Ofi30s = _ofi30.Sum(now);

            // ── QI / micro-price / urgency ──
            s.QiL1 = (bq + aq) > 0 ? (bq - aq) / (bq + aq) : 0;
            double wb = 0, wa = 0;
            for (int i = 0; i < 5; i++)
            {
                double w = 1.0 / (i + 1);    // 거리 가중(레벨 역수)
                wb += d.BidRem[i] * w; wa += d.OfferRem[i] * w;
            }
            s.QiW5 = (wb + wa) > 0 ? (wb - wa) / (wb + wa) : 0;
            s.Mid = (bid + ask) / 2.0;
            s.MicroPx = (bq + aq) > 0 ? (ask * bq + bid * aq) / (bq + aq) : s.Mid;   // Stoikov
            s.MicroDevTicks = (s.MicroPx - s.Mid) / Tick;
            s.Spread = ask - bid;
            s.Urgency = s.Spread * Math.Abs(s.QiW5);

            // ── 10초 RV ──
            if (_prevMid > 0 && s.Mid > 0)
            {
                double r = Math.Log(s.Mid / _prevMid);
                _rv10.Add(now, r * r);
            }
            _prevMid = s.Mid;
            s.Rv10 = Math.Sqrt(Math.Max(0, _rv10.Sum(now)));

            // ── 1초 샘플링 → z-score 히스토리 ──
            if (now - _lastSampleMs >= 1000)
            {
                _lastSampleMs = now;
                _ofi5Hist.Add(s.Ofi5s);
                _rvHist.Add(s.Rv10);
                _basisHist.Add((double)_lastKasis);
                _totRemHist.Add(d.TotBidRem + d.TotOfferRem);
            }
            s.Ofi5sZ = _ofi5Hist.Z(s.Ofi5s);
            s.Rv10Z = _rvHist.Z(s.Rv10);
            s.Kasis = (double)_lastKasis;
            s.BasisZ = _basisHist.Z((double)_lastKasis);
            s.Bear = _bear;

            // ── gap_flag: RV 버스트 ∧ 스프레드 확대 ∧ 호가 총잔량 급감 (5초 유지) ──
            double remAvg = _totRemHist.Mean();
            bool trigger = s.Rv10Z >= GapRvZ
                        && s.Spread >= GapSpreadMin
                        && remAvg > 0 && (d.TotBidRem + d.TotOfferRem) < GapRemRatio * remAvg;
            if (trigger) _gapSinceMs = now;
            s.GapFlag = _gapSinceMs > 0 && (now - _gapSinceMs) <= GapHoldMs;

            _prevDepth = d;
            _snap = s;   // 참조 교체(원자적) — 읽기 스레드 안전
        }

        /// <summary>시간 윈도우 러닝 합 — enqueue 시 더하고 만료분 빼기.</summary>
        private sealed class WindowSum
        {
            private readonly int _ms;
            private readonly Queue<Entry> _q = new Queue<Entry>();
            private double _sum;
            private struct Entry { public long T; public double V; }
            public WindowSum(int windowMs) { _ms = windowMs; }
            public void Add(long now, double v) { _q.Enqueue(new Entry { T = now, V = v }); _sum += v; Prune(now); }
            public double Sum(long now) { Prune(now); return _sum; }
            private void Prune(long now)
            {
                while (_q.Count > 0 && now - _q.Peek().T > _ms) _sum -= _q.Dequeue().V;
            }
        }

        /// <summary>고정 크기 롤링 평균/표준편차 (1초 샘플용).</summary>
        private sealed class RollingStats
        {
            private readonly int _cap;
            private readonly Queue<double> _q = new Queue<double>();
            private double _sum, _sumSq;
            public RollingStats(int cap) { _cap = cap; }
            public void Add(double v)
            {
                _q.Enqueue(v); _sum += v; _sumSq += v * v;
                if (_q.Count > _cap) { double o = _q.Dequeue(); _sum -= o; _sumSq -= o * o; }
            }
            public double Mean() { return _q.Count > 0 ? _sum / _q.Count : 0; }
            public double Z(double v)
            {
                int n = _q.Count;
                if (n < 30) return 0;   // 표본 부족 시 중립(신호 발화 방지)
                double m = _sum / n;
                double varr = Math.Max(1e-12, _sumSq / n - m * m);
                return (v - m) / Math.Sqrt(varr);
            }
        }
    }
}
